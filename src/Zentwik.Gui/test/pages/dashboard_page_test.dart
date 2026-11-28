import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';
import 'package:zentwik_gui/pages/dashboard_page.dart';
import 'package:zentwik_gui/theme/theme.dart';
import 'package:zentwik_gui/workspace/timeline_event.dart';
import 'package:zentwik_gui/workspace/workspace.dart';
import 'package:zentwik_gui/workspace/workspace_store.dart';

/// 卡片内容体用纯文本替身：这些测试只关心布局、编辑与时间线，
/// 不关心真实卡片画了什么。
CardRegistry stubRegistry() => CardRegistry({
      for (final kind in DashboardKind.values)
        kind.wire: (context, item) => Text('body:${kind.wire}'),
    });

Future<Workspace> testWorkspace({Map<String, Object?>? data}) async {
  final workspace = Workspace(store: MemoryWorkspaceStore(data));
  await workspace.load();
  return workspace;
}

Widget harness(Workspace workspace) => MaterialApp(
      theme: buildZentwikTheme(),
      home: Scaffold(
        body: DashboardPage(
          workspace: workspace,
          registry: stubRegistry(),
        ),
      ),
    );

void main() {
  testWidgets('宽屏默认只渲染脉搏带与状态摘要，且平时不出现任何编辑控件', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final workspace = await testWorkspace();
    await tester.pumpWidget(harness(workspace));
    await tester.pumpAndSettle();

    for (final item in workspace.board.items) {
      expect(
        find.text(item.kind.label),
        findsOneWidget,
        reason: '缺少 ${item.kind.wire}',
      );
    }
    // 默认不把监控指标全堆上去：这类组件留给用户自己添加。
    expect(find.text(DashboardKind.coreClocks.label), findsNothing);
    expect(find.text(DashboardKind.processorUsage.label), findsNothing);

    // 平时零按钮：没有拖拽把手、没有卡片菜单（§2.8 一页一个主操作）。
    expect(find.byIcon(Icons.drag_indicator), findsNothing);
    expect(find.byIcon(Icons.more_vert), findsNothing);
    expect(find.text('编辑布局'), findsOneWidget);
    expect(find.text('完成'), findsNothing);
  });

  testWidgets('进入编辑态后出现把手与菜单，退出后消失', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final workspace = await testWorkspace();
    await tester.pumpWidget(harness(workspace));
    await tester.pumpAndSettle();

    await tester.tap(find.text('编辑布局'));
    await tester.pumpAndSettle();

    expect(workspace.editing, isTrue);
    expect(find.text('完成'), findsOneWidget);
    expect(find.text('添加组件'), findsOneWidget);
    expect(
      find.byIcon(Icons.drag_indicator),
      findsNWidgets(workspace.board.items.length),
    );
    expect(
      find.byIcon(Icons.more_vert),
      findsNWidgets(workspace.board.items.length),
    );

    await tester.tap(find.text('完成'));
    await tester.pumpAndSettle();
    expect(workspace.editing, isFalse);
    expect(find.byIcon(Icons.drag_indicator), findsNothing);
  });

  testWidgets('菜单可以移出组件，并从布局里真正删除', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final workspace = await testWorkspace();
    await tester.pumpWidget(harness(workspace));
    await tester.pumpAndSettle();
    await tester.tap(find.text('编辑布局'));
    await tester.pumpAndSettle();

    final card = find.ancestor(
      of: find.text(DashboardKind.cpuUsage.label),
      matching: find.byType(DashboardCardShell),
    );
    await tester.tap(
      find.descendant(of: card, matching: find.byIcon(Icons.more_vert)),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('移出主工作区'));
    await tester.pumpAndSettle();

    expect(workspace.board.contains(DashboardKind.cpuUsage), isFalse);
    expect(find.text(DashboardKind.cpuUsage.label), findsNothing);
  });

  testWidgets('添加面板只列出未添加的组件，选中后回到主工作区', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final workspace = await testWorkspace();
    workspace.remove(DashboardKind.moduleStatus);
    await tester.pumpWidget(harness(workspace));
    await tester.pumpAndSettle();
    await tester.tap(find.text('编辑布局'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('添加组件'));
    await tester.pumpAndSettle();

    // 已被移除的那一项出现在对话框里；已在首页上的不应重复出现。
    expect(
      find.descendant(
        of: find.byType(AlertDialog),
        matching: find.text(DashboardKind.moduleStatus.label),
      ),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byType(AlertDialog),
        matching: find.text(DashboardKind.cpuUsage.label),
      ),
      findsNothing,
    );

    await tester.tap(find.text(DashboardKind.moduleStatus.label));
    await tester.pumpAndSettle();

    expect(workspace.board.contains(DashboardKind.moduleStatus), isTrue);
    expect(find.text(DashboardKind.moduleStatus.label), findsOneWidget);
  });

  testWidgets('主工作区全空时给出带主操作的空态', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final workspace = await testWorkspace();
    for (final kind in DashboardKind.values) {
      workspace.remove(kind);
    }
    await tester.pumpWidget(harness(workspace));
    await tester.pumpAndSettle();

    expect(find.text('主工作区还没有组件'), findsOneWidget);
    expect(find.text('添加组件'), findsOneWidget);

    // 让 400ms 的防抖保存计时器跑完，否则测试结束时会报 Timer 仍挂起。
    await tester.pump(const Duration(milliseconds: 500));
  });

  testWidgets('时间线可折叠、可隐藏，隐藏后主工作区拿到整幅宽度', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final workspace = await testWorkspace();
    workspace.record(
      category: TimelineCategory.connection,
      severity: TimelineSeverity.success,
      title: '已连接到 Agent 服务',
    );
    await tester.pumpWidget(harness(workspace));
    await tester.pumpAndSettle();

    // 量的是网格可用宽度而不是某张卡：列数会随宽度变化，
    // 卡片自身反而可能因为列变多而变窄。
    final fullWidth = tester.getSize(find.byType(MasonryGrid)).width;

    // 折叠 → 只剩窄轨，主工作区拿回宽度。
    await tester.tap(find.byTooltip('折叠时间线'));
    await tester.pumpAndSettle();
    expect(workspace.timelineCollapsed, isTrue);
    expect(find.byTooltip('展开时间线'), findsOneWidget);
    expect(
      tester.getSize(find.byType(MasonryGrid)).width,
      greaterThan(fullWidth),
    );

    // 展开后恢复。
    await tester.tap(find.byTooltip('展开时间线'));
    await tester.pumpAndSettle();
    expect(workspace.timelineCollapsed, isFalse);

    // 隐藏 → 面板整块消失，工具栏出现恢复入口。
    await tester.tap(find.byTooltip('隐藏时间线'));
    await tester.pumpAndSettle();
    expect(workspace.timelineHidden, isTrue);
    expect(find.byTooltip('展开时间线'), findsNothing);
    expect(find.byTooltip('显示时间线'), findsOneWidget);

    await tester.tap(find.byTooltip('显示时间线'));
    await tester.pumpAndSettle();
    expect(workspace.timelineHidden, isFalse);

    await tester.pump(const Duration(milliseconds: 500));
  });

  testWidgets('拖卡片右边缘按列吸附改宽度，拖下边缘改高度', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final workspace = await testWorkspace();
    await tester.pumpWidget(harness(workspace));
    await tester.pumpAndSettle();
    await tester.tap(find.text('编辑布局'));
    await tester.pumpAndSettle();

    final kind = workspace.board.items.first.kind;
    int spanOf() =>
        workspace.board.items.firstWhere((i) => i.kind == kind).span;
    double? heightOf() =>
        workspace.board.items.firstWhere((i) => i.kind == kind).height;

    final rect = tester.getRect(
      find.ancestor(
        of: find.text(kind.label),
        matching: find.byType(DashboardCardShell),
      ),
    );
    expect(spanOf(), 1);
    expect(heightOf(), isNull, reason: '默认应为自适应高度');

    // 右边缘：往右拖超过一个列宽 → 跨列 +1。
    await tester.dragFrom(
      Offset(rect.right - 4, rect.center.dy),
      const Offset(400, 0),
    );
    await tester.pumpAndSettle();
    expect(spanOf(), 2, reason: '应吸附到两列');

    // 下边缘：往下拖 → 变成显式高度，且高于原来的自适应高度。
    final grown = tester.getRect(
      find.ancestor(
        of: find.text(kind.label),
        matching: find.byType(DashboardCardShell),
      ),
    );
    final before = grown.height;
    await tester.dragFrom(
      Offset(grown.center.dx, grown.bottom - 4),
      const Offset(0, 120),
    );
    await tester.pumpAndSettle();

    expect(heightOf(), isNotNull, reason: '拖下边缘应转为显式高度');
    expect(heightOf()!, greaterThan(before));
    expect(heightOf()! % 8, 0, reason: '高度按 8px 吸附');

    await tester.pump(const Duration(milliseconds: 500));
  });

  testWidgets('折叠状态与宽度变化会写入本地存储', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final store = MemoryWorkspaceStore();
    final workspace = Workspace(store: store);
    await workspace.load();

    await tester.pumpWidget(harness(workspace));
    await tester.pumpAndSettle();

    workspace.setTimelineWidth(400);
    workspace.toggleTimelineHidden();
    await tester.pump(const Duration(milliseconds: 500));

    expect(store.data, isNotNull, reason: '改动应被防抖写盘');
    expect((store.data!['timeline']! as Map)['width'], 400);
    expect((store.data!['timeline']! as Map)['hidden'], isTrue);
  });

  testWidgets('重启后按保存的顺序与跨度恢复布局', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final store = MemoryWorkspaceStore();
    final first = Workspace(store: store);
    await first.load();
    final last = first.board.items.length - 1;
    first.move(0, last);
    first.resizeSpan(DashboardKind.cpuUsage, 2, columns: 4);
    await tester.pump(const Duration(milliseconds: 500));

    final second = Workspace(store: store);
    await second.load();

    expect(second.board, first.board);
    expect(second.board.items.last.kind, DashboardKind.cpuUsage);
    expect(
      second.board.items
          .firstWhere((i) => i.kind == DashboardKind.cpuUsage)
          .span,
      2,
    );
  });
}
