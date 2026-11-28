import 'package:flutter/material.dart';

import '../state/connection_state.dart';
import '../theme/tokens.dart';

/// 根据连接状态机状态展示顶部提示横幅；`connected` 与 `disconnected` 保持静默。
class ConnectionBanner extends StatelessWidget {
  const ConnectionBanner({
    super.key,
    required this.status,
    this.attempt = 0,
    this.onRetry,
  });

  final String status;
  final int attempt;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    if (status == ConnectionStatuses.connected ||
        status == ConnectionStatuses.disconnected) {
      return const SizedBox.shrink();
    }

    final isFailed = status == ConnectionStatuses.failed;
    final accentColor =
        isFailed ? ZentwikTokens.danger : ZentwikTokens.accent;
    final message = switch (status) {
      ConnectionStatuses.connecting => '正在连接后台服务…',
      ConnectionStatuses.reconnecting => attempt > 0
          ? '连接已断开，正在执行第 $attempt 次重连…'
          : '连接已断开，正在重新连接…',
      ConnectionStatuses.failed => '无法连接到后台服务，请确认服务已启动后重试。',
      _ => '',
    };

    if (message.isEmpty) {
      return const SizedBox.shrink();
    }

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.symmetric(
        horizontal: ZentwikTokens.spaceLg,
        vertical: ZentwikTokens.spaceSm,
      ),
      decoration: const BoxDecoration(
        color: ZentwikTokens.surface,
        border: Border(
          bottom: BorderSide(color: ZentwikTokens.border),
        ),
      ),
      child: Row(
        children: [
          Icon(
            isFailed ? Icons.error_outline : Icons.sync,
            size: 16,
            color: accentColor,
          ),
          const SizedBox(width: ZentwikTokens.spaceSm),
          Expanded(
            child: Text(
              message,
              style: const TextStyle(
                fontSize: ZentwikTokens.fontBody,
                color: ZentwikTokens.textPrimary,
              ),
            ),
          ),
          if (isFailed && onRetry != null)
            TextButton(
              onPressed: onRetry,
              child: const Text('重试'),
            ),
        ],
      ),
    );
  }
}
