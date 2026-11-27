import 'dart:async';
import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';
import 'dart:typed_data';

import 'frame_channel.dart';

// ---- Win32 常量 ----
const int _genericRead = 0x80000000;
const int _genericWrite = 0x40000000;
const int _openExisting = 3;
const int _fileAttributeNormal = 0x80;
const int _errorPipeBusy = 231;
const int _invalidHandleValue = -1;
const int _readChunk = 64 * 1024;

// ---- 主 isolate 侧绑定的 kernel32 符号（只查一次）----
final DynamicLibrary _kernel32 = DynamicLibrary.open('kernel32.dll');

typedef _CreateFileNative = IntPtr Function(
    Pointer<Uint16>, Uint32, Uint32, Pointer<Void>, Uint32, Uint32, IntPtr);
typedef _CreateFileDart = int Function(
    Pointer<Uint16>, int, int, Pointer<Void>, int, int, int);

final _CreateFileDart _createFileW =
    _kernel32.lookupFunction<_CreateFileNative, _CreateFileDart>('CreateFileW');

final int Function(int) _closeHandle =
    _kernel32.lookupFunction<Int32 Function(IntPtr), int Function(int)>('CloseHandle');

final int Function() _getLastError =
    _kernel32.lookupFunction<Uint32 Function(), int Function()>('GetLastError');

final _WriteFileDart _writeFile = _kernel32.lookupFunction<_WriteFileNative, _WriteFileDart>('WriteFile');

typedef _WriteFileNative = Int32 Function(
    IntPtr, Pointer<Uint8>, Uint32, Pointer<Uint32>, Pointer<Void>);
typedef _WriteFileDart = int Function(int, Pointer<Uint8>, int, Pointer<Uint32>, Pointer<Void>);

// 不引入 package:ffi：借 msvcrt 的 malloc/free，少一个依赖。
final DynamicLibrary _msvcrt = DynamicLibrary.open('msvcrt.dll');
final Pointer<Void> Function(int) _malloc =
    _msvcrt.lookupFunction<Pointer<Void> Function(IntPtr), Pointer<Void> Function(int)>('malloc');
final void Function(Pointer<Void>) _free =
    _msvcrt.lookupFunction<Void Function(Pointer<Void>), void Function(Pointer<Void>)>('free');

Pointer<Uint8> _allocBytes(int bytes) {
  final ptr = _malloc(bytes <= 0 ? 1 : bytes);
  if (ptr == nullptr) {
    throw StateError('malloc 失败：$bytes 字节');
  }

  return ptr.cast<Uint8>();
}

Pointer<Uint16> _toUtf16(String value) {
  final units = value.codeUnits;
  final bytes = _allocBytes((units.length + 1) * 2);
  final ptr = bytes.cast<Uint16>();
  final list = ptr.asTypedList(units.length + 1);
  list.setAll(0, units);
  list[units.length] = 0;
  return ptr;
}

/// 通过命名管道连 Windows 服务。
///
/// 读用**独立 isolate 里的阻塞 ReadFile**：没有数据时该 isolate 睡在系统调用里，
/// 不轮询、不空转，CPU 为 0。写在本 isolate 串行排队，避免两帧字节交错。
class Win32PipeChannel implements FrameChannel {
  Win32PipeChannel({this.pipeName = r'\\.\pipe\zentwik-agent'});

  final String pipeName;

  final FrameBuffer _buffer = FrameBuffer();
  final List<Completer<Uint8List?>> _waiters = [];
  Future<void> _writeTail = Future<void>.value();
  Isolate? _reader;
  ReceivePort? _messages;
  int _handle = _invalidHandleValue;
  bool _closed = false;
  bool _eof = false;

  bool get isConnected => _handle != _invalidHandleValue && !_closed;

  @override
  Future<void> connect({Duration timeout = const Duration(seconds: 3)}) async {
    if (!Platform.isWindows) {
      throw UnsupportedError('命名管道只在 Windows 上可用');
    }

    final deadline = DateTime.now().add(timeout);
    while (true) {
      final handle = _openPipe(pipeName);
      if (handle != _invalidHandleValue) {
        _handle = handle;
        break;
      }

      final error = _getLastError();
      if (error != _errorPipeBusy || DateTime.now().isAfter(deadline)) {
        throw PipeConnectException(pipeName, error);
      }

      await Future<void>.delayed(const Duration(milliseconds: 50));
    }

    final messages = ReceivePort();
    _messages = messages;
    messages.listen(_onChunk, onDone: _onReaderGone);

    _reader = await Isolate.spawn(
      _readLoop,
      _ReaderRequest(messages.sendPort, _handle),
      debugName: 'zentwik-pipe-reader',
    );
  }

  @override
  Future<Uint8List?> readFrame() async {
    final ready = _takeFrame();
    if (ready != null) {
      return ready;
    }

    if (_eof || _closed) {
      return null;
    }

    final completer = Completer<Uint8List?>();
    _waiters.add(completer);
    return completer.future;
  }

  Uint8List? _takeFrame() {
    try {
      return _buffer.tryTake();
    } on FrameTruncatedException {
      // 长度前缀非法：协议已不可恢复，按关闭处理。
      _eof = true;
      _completeAll(null);
      return null;
    }
  }

  void _onChunk(Object? message) {
    if (message == null) {
      _eof = true;
      _completeAll(null);
      return;
    }

    _buffer.add(message as Uint8List);
    _pump();
  }

  void _pump() {
    while (_waiters.isNotEmpty) {
      final frame = _takeFrame();
      if (frame == null) {
        return;
      }

      final waiter = _waiters.removeAt(0);
      if (!waiter.isCompleted) {
        waiter.complete(frame);
      }
    }
  }

  void _completeAll(Uint8List? value) {
    while (_waiters.isNotEmpty) {
      final waiter = _waiters.removeAt(0);
      if (!waiter.isCompleted) {
        waiter.complete(value);
      }
    }
  }

  void _onReaderGone() {
    _eof = true;
    _completeAll(null);
  }

  @override
  Future<void> writeFrame(Uint8List body) {
    final frame = encodeFrame(body);
    final next = _writeTail.then((_) => _write(frame));
    _writeTail = next.catchError((Object _) {});
    return next;
  }

  Future<void> _write(Uint8List frame) async {
    if (!isConnected) {
      throw const PipeClosedException();
    }

    final bytes = _allocBytes(frame.length);
    final written = _allocBytes(4).cast<Uint32>();
    try {
      bytes.asTypedList(frame.length).setAll(0, frame);
      var offset = 0;
      while (offset < frame.length) {
        final ok = _writeFile(
          _handle,
          bytes + offset,
          frame.length - offset,
          written,
          nullptr,
        );
        if (ok == 0) {
          throw PipeWriteException(_getLastError());
        }

        final count = written.value;
        if (count == 0) {
          throw PipeWriteException(_getLastError());
        }

        offset += count;
      }
    } finally {
      _free(bytes.cast<Void>());
      _free(written.cast<Void>());
    }
  }

  @override
  Future<void> close() async {
    if (_closed) {
      return;
    }

    _closed = true;
    final handle = _handle;
    _handle = _invalidHandleValue;
    if (handle != _invalidHandleValue && Platform.isWindows) {
      // 关句柄会让阻塞中的 ReadFile 返回错误，读 isolate 自行退出。
      _closeHandle(handle);
    }

    final reader = _reader;
    _reader = null;
    if (reader != null) {
      reader.kill(priority: Isolate.immediate);
    }

    _messages?.close();
    _messages = null;
    _eof = true;
    _completeAll(null);
  }

  static int _openPipe(String name) {
    final namePtr = _toUtf16(name);
    try {
      return _createFileW(
        namePtr,
        _genericRead | _genericWrite,
        0,
        nullptr,
        _openExisting,
        _fileAttributeNormal,
        0,
      );
    } finally {
      _free(namePtr.cast<Void>());
    }
  }
}

/// 读 isolate 的入参。只含 int 与 SendPort，可跨 isolate 传递。
final class _ReaderRequest {
  const _ReaderRequest(this.port, this.handle);

  final SendPort port;
  final int handle;
}

/// 读循环：阻塞 ReadFile，整块回传。
///
/// 必须是顶层函数：isolate 内重新查符号，FFI 句柄不跨 isolate 共享。
void _readLoop(_ReaderRequest request) {
  final lib = DynamicLibrary.open('kernel32.dll');
  final readFile = lib.lookupFunction<
      Int32 Function(IntPtr, Pointer<Uint8>, Uint32, Pointer<Uint32>, Pointer<Void>),
      int Function(int, Pointer<Uint8>, int, Pointer<Uint32>, Pointer<Void>)>('ReadFile');

  final buffer = _allocBytes(_readChunk);
  final readPtr = _allocBytes(4).cast<Uint32>();

  try {
    while (true) {
      final ok = readFile(request.handle, buffer, _readChunk, readPtr, nullptr);
      if (ok == 0) {
        break;
      }

      final read = readPtr.value;
      if (read == 0) {
        break;
      }

      request.port.send(Uint8List.fromList(buffer.asTypedList(read)));
    }
  } finally {
    _free(buffer.cast<Void>());
    _free(readPtr.cast<Void>());
    request.port.send(null);
  }
}

class PipeConnectException implements Exception {
  const PipeConnectException(this.pipe, this.errorCode);

  final String pipe;
  final int errorCode;

  @override
  String toString() => '连接管道失败：$pipe（Win32 错误 $errorCode）';
}

class PipeWriteException implements Exception {
  const PipeWriteException(this.errorCode);

  final int errorCode;

  @override
  String toString() => '管道写入失败（Win32 错误 $errorCode）';
}

class PipeClosedException implements Exception {
  const PipeClosedException();

  @override
  String toString() => '管道已关闭';
}
