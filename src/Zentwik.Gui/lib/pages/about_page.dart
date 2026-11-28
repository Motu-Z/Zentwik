import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/agent_status_dto.dart';
import '../state/connection_state.dart';
import '../theme/tokens.dart';

/// 关于页：客观展示代理版本、运行时长、命名管道与当前连接状态。
class AboutPage extends StatelessWidget {
  const AboutPage({
    super.key,
    this.status,
    this.connectionStatus = ConnectionStatuses.disconnected,
  });

  static const String kPipeName = 'zentwik-agent';

  final AgentStatusDto? status;
  final String connectionStatus;

  String _formatUptimeOrStart(AgentStatusDto? current) {
    if (current == null) {
      return kMissingValue;
    }
    final uptime = current.uptimeSeconds;
    if (uptime != null) {
      return '$uptime 秒';
    }
    final startedAt = current.startedAt;
    if (startedAt != null) {
      return startedAt.toIso8601String();
    }
    return kMissingValue;
  }

  String _connectionLabel(String value) {
    return switch (value) {
      ConnectionStatuses.connected => '已连接',
      ConnectionStatuses.connecting => '正在连接',
      ConnectionStatuses.reconnecting => '正在重连',
      ConnectionStatuses.failed => '连接失败',
      ConnectionStatuses.disconnected => '未连接',
      _ => kMissingValue,
    };
  }

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(ZentwikTokens.spaceLg),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text(
            '关于',
            style: TextStyle(
              fontSize: ZentwikTokens.fontTitle,
              fontWeight: FontWeight.w600,
              color: ZentwikTokens.textPrimary,
            ),
          ),
          const SizedBox(height: ZentwikTokens.spaceLg),
          _AboutRow(
            label: '代理版本',
            value: status?.version ?? kMissingValue,
          ),
          _AboutRow(
            label: '运行时长',
            value: _formatUptimeOrStart(status),
          ),
          const _AboutRow(
            label: '命名管道',
            value: kPipeName,
          ),
          _AboutRow(
            label: '连接状态',
            value: _connectionLabel(connectionStatus),
          ),
        ],
      ),
    );
  }
}

class _AboutRow extends StatelessWidget {
  const _AboutRow({
    required this.label,
    required this.value,
  });

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: ZentwikTokens.spaceSm),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Text(
            label,
            style: const TextStyle(
              fontSize: ZentwikTokens.fontBody,
              color: ZentwikTokens.textSecondary,
            ),
          ),
          Text(
            value,
            style: const TextStyle(
              fontSize: ZentwikTokens.fontBody,
              color: ZentwikTokens.textPrimary,
            ),
          ),
        ],
      ),
    );
  }
}
