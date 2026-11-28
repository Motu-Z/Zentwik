@Tags(['smoke'])
library;

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';
import 'package:zentwik_gui/workspace/workspace.dart';
import 'package:zentwik_gui/workspace/workspace_store.dart';

/// 手工检查：真实文件系统上的布局持久化。
///
/// 单元测试用 `MemoryWorkspaceStore`，覆盖不到「目录不存在、编码、原子改名」
/// 这些只在真机上出问题的地方。这个检查走一遍真实磁盘，但不碰用户的
/// `%LOCALAPPDATA%\Zentwik\gui\workspace.json`（用 .tmp 下的临时路径）。
///
/// 运行：`flutter test test/manual/workspace_store_check.dart --tags smoke`
void main() {
  test('默认路径落在 %LOCALAPPDATA%\\Zentwik\\gui 下', () {
    final path = FileWorkspaceStore.defaultPath().replaceAll('\\', '/');
    expect(path, endsWith('Zentwik/gui/workspace.json'));
    // ignore: avoid_print
    print('默认布局文件：${FileWorkspaceStore.defaultPath()}');
  });

  test('真实磁盘往返：写入 → 读回 → 目录自动创建', () async {
    final dir = Directory('.tmp/workspace-check')
      ..createSync(recursive: true);
    final file = File('${dir.path}/nested/workspace.json');
    if (file.existsSync()) {
      file.deleteSync();
    }

    final store = FileWorkspaceStore(path: file.path);
    expect(await store.read(), isNull, reason: '文件不存在时应返回 null 而不是抛异常');

    final workspace = Workspace(store: store);
    await workspace.load();
    workspace.move(0, 4);
    workspace.setHeight(DashboardKind.memory, 208);
    workspace.setTimelineWidth(400);

    // 走真实防抖计时器，等它落盘。
    await Future<void>.delayed(const Duration(milliseconds: 700));
    expect(file.existsSync(), isTrue, reason: '保存后文件应存在（父目录自动创建）');

    final reloaded = Workspace(store: FileWorkspaceStore(path: file.path));
    await reloaded.load();
    expect(reloaded.board, workspace.board);
    expect(reloaded.timelineWidth, 400);
    expect(
      reloaded.board.items.firstWhere((i) => i.kind == DashboardKind.memory).height,
      208,
    );

    // ignore: avoid_print
    print('往返成功：${file.path}（${file.lengthSync()} 字节）');

    dir.deleteSync(recursive: true);
  });

  test('坏文件不会把布局读崩', () async {
    final dir = Directory('.tmp/workspace-check-bad')
      ..createSync(recursive: true);
    final file = File('${dir.path}/workspace.json')
      ..writeAsStringSync('{"board": {"items": "这不是数组"}}');

    final workspace = Workspace(store: FileWorkspaceStore(path: file.path));
    await workspace.load();
    expect(
      workspace.board,
      DashboardBoard.defaults(),
      reason: '解析失败应退回默认布局',
    );

    file.writeAsStringSync('{ 半个 JSON');
    final again = Workspace(store: FileWorkspaceStore(path: file.path));
    await again.load();
    expect(again.board, DashboardBoard.defaults());

    dir.deleteSync(recursive: true);
  });
}
