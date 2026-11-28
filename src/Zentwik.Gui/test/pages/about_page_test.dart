import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/format/units.dart';
import 'package:zentwik_gui/pages/about_page.dart';
import 'package:zentwik_gui/protocol/models/agent_status_dto.dart';
import 'package:zentwik_gui/state/connection_state.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  Widget wrap(Widget child) {
    return MaterialApp(
      theme: buildZentwikTheme(),
      home: Scaffold(body: child),
    );
  }

  testWidgets('AboutPage 在无 AgentStatusDto 时显示 — 与管道名', (tester) async {
    await tester.pumpWidget(wrap(const AboutPage()));

    expect(find.text('关于'), findsOneWidget);
    expect(find.text('zentwik-agent'), findsOneWidget);
    expect(find.text('未连接'), findsOneWidget);
    expect(find.text(kMissingValue), findsNWidgets(2));
  });

  testWidgets('AboutPage 在有 AgentStatusDto 时显示版本、运行时长与连接状态',
      (tester) async {
    const status = AgentStatusDto(
      version: '0.1.0',
      uptimeSeconds: 6879,
    );

    await tester.pumpWidget(
      wrap(
        const AboutPage(
          status: status,
          connectionStatus: ConnectionStatuses.connected,
        ),
      ),
    );

    expect(find.text('0.1.0'), findsOneWidget);
    expect(find.text('1 小时 54 分'), findsOneWidget);
    expect(find.text('zentwik-agent'), findsOneWidget);
    expect(find.text('已连接'), findsOneWidget);
  });
}
