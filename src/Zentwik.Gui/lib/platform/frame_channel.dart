import 'dart:async';
import 'dart:typed_data';

/// 一条按帧收发的双向通道。
///
/// 上层（协议客户端）只看到**完整帧的正文**，不关心分帧细节与底层是 Windows
/// 命名管道还是测试用的内存实现。这样协议层可以在非 Windows 环境完整测试。
abstract interface class FrameChannel {
  /// 建立连接。[timeout] 到期抛 [TimeoutException]。
  Future<void> connect({Duration timeout = const Duration(seconds: 3)});

  /// 读下一帧正文。返回 `null` 表示对端已正常关闭。
  Future<Uint8List?> readFrame();

  /// 写一帧正文。超过 4 MiB 抛 [FrameTooLargeException]。
  Future<void> writeFrame(Uint8List body);

  /// 关闭通道。可重复调用。
  Future<void> close();
}

/// 单帧上限，与服务端 `ProtocolInfo.MaxFrameBytes` 一致。
const int kMaxFrameBytes = 4 * 1024 * 1024;

class FrameTooLargeException implements Exception {
  FrameTooLargeException(this.length);

  final int length;

  @override
  String toString() => '帧过大：$length 字节（上限 $kMaxFrameBytes）';
}

class FrameTruncatedException implements Exception {
  @override
  String toString() => '帧被截断：连接在正文读完前关闭';
}

/// 把 4 字节小端长度前缀与正文拼成可写入的字节。
Uint8List encodeFrame(Uint8List body) {
  if (body.length > kMaxFrameBytes) {
    throw FrameTooLargeException(body.length);
  }

  final frame = Uint8List(4 + body.length);
  ByteData.view(frame.buffer, 0, 4).setInt32(0, body.length, Endian.little);
  frame.setRange(4, frame.length, body);
  return frame;
}

/// 增量拆帧缓冲：把任意长度的字节块喂进来，按需取出完整帧正文。
///
/// 通道实现（命名管道、内存假实现）都必须走这里，保证两侧分帧语义一致。
class FrameBuffer {
  final BytesBuilder _pending = BytesBuilder(copy: true);

  /// 追加原始字节。
  void add(List<int> chunk) => _pending.add(chunk);

  /// 取出下一帧正文；数据不足返回 `null`。
  Uint8List? tryTake() {
    final bytes = _pending.toBytes();
    if (bytes.length < 4) {
      return null;
    }

    final length = ByteData.view(bytes.buffer, bytes.offsetInBytes, 4).getInt32(0, Endian.little);
    if (length <= 0 || length > kMaxFrameBytes) {
      throw FrameTruncatedException();
    }

    if (bytes.length < 4 + length) {
      return null;
    }

    final body = Uint8List.sublistView(bytes, 4, 4 + length);
    final rest = Uint8List.sublistView(bytes, 4 + length);
    _pending
      ..clear()
      ..add(rest);
    return body;
  }

  /// 待处理字节数，调试用。
  int get pendingBytes => _pending.length;
}

/// 内存通道：按请求脚本回放响应，供协议层单元测试与界面预览使用。
///
/// 不依赖 Windows，Linux 上可直接跑。
class InMemoryFrameChannel implements FrameChannel {
  InMemoryFrameChannel({List<Uint8List>? replies}) : _queued = [...?replies];

  final List<Uint8List> _queued;
  final List<Uint8List> written = [];
  bool _connected = false;

  /// 已写入的帧正文，按顺序保留，便于断言客户端发出的请求。
  List<Uint8List> get sent => List.unmodifiable(written);

  /// 追加一帧待读内容（可在运行中追加，模拟事件推送）。
  void push(Uint8List body) => _queued.add(body);

  @override
  Future<void> connect({Duration timeout = const Duration(seconds: 3)}) async {
    _connected = true;
  }

  @override
  Future<Uint8List?> readFrame() async {
    if (!_connected) {
      throw StateError('通道尚未连接');
    }

    // 队列空即模拟对端关闭，调用方的读取循环据此结束。
    return _queued.isEmpty ? null : _queued.removeAt(0);
  }

  @override
  Future<void> writeFrame(Uint8List body) async {
    if (!_connected) {
      throw StateError('通道尚未连接');
    }

    if (body.length > kMaxFrameBytes) {
      throw FrameTooLargeException(body.length);
    }

    written.add(body);
  }

  @override
  Future<void> close() async {
    _connected = false;
  }
}
