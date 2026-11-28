import 'dart:convert';
import 'dart:io';

/// 工作区持久化的读写口子。
///
/// 抽成接口是为了让布局单测不碰真实磁盘：测试用 [MemoryWorkspaceStore]，
/// 生产用 [FileWorkspaceStore]。读取失败一律当作「没有保存过」，
/// 不让一个坏文件把首页锁死。
abstract class WorkspaceStore {
  Future<Map<String, Object?>?> read();

  Future<void> write(Map<String, Object?> data);
}

/// 只在内存里留一份，供测试使用。
class MemoryWorkspaceStore implements WorkspaceStore {
  MemoryWorkspaceStore([this.data]);

  Map<String, Object?>? data;
  int writeCount = 0;

  @override
  Future<Map<String, Object?>?> read() async => data;

  @override
  Future<void> write(Map<String, Object?> value) async {
    data = value;
    writeCount++;
  }
}

/// 写 `%LOCALAPPDATA%\Zentwik\gui\workspace.json`。
///
/// 位置与后端数据目录（`%LOCALAPPDATA%\Zentwik`）同级但不进 `state.json`：
/// 首页布局是纯界面偏好，不该和服务端设置混在一起被协议接管。
class FileWorkspaceStore implements WorkspaceStore {
  FileWorkspaceStore({String? path}) : _path = path ?? defaultPath();

  final String _path;

  static String defaultPath() {
    final base = Platform.environment['LOCALAPPDATA'] ??
        Platform.environment['APPDATA'] ??
        Directory.current.path;
    return '$base${Platform.pathSeparator}Zentwik'
        '${Platform.pathSeparator}gui${Platform.pathSeparator}workspace.json';
  }

  String get path => _path;

  @override
  Future<Map<String, Object?>?> read() async {
    try {
      final file = File(_path);
      if (!await file.exists()) {
        return null;
      }
      final text = await file.readAsString();
      if (text.trim().isEmpty) {
        return null;
      }
      final decoded = jsonDecode(text);
      return decoded is Map<String, Object?> ? decoded : null;
    } on Object {
      // 半截文件、编码错误、权限问题都退回默认布局。
      return null;
    }
  }

  @override
  Future<void> write(Map<String, Object?> data) async {
    try {
      final file = File(_path);
      await file.parent.create(recursive: true);
      // 先写临时文件再改名：断电时不会留下半个 JSON 把布局读坏。
      final temp = File('$_path.tmp');
      await temp.writeAsString(jsonEncode(data), flush: true);
      await temp.rename(_path);
    } on Object {
      // 持久化失败只影响下次启动的布局，不影响当前使用，静默降级。
    }
  }
}
