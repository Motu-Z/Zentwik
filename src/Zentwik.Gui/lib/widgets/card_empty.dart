import 'package:material_ui/material_ui.dart';

import '../theme/theme.dart';

/// 九张仪表盘卡片共用的空态提示。
class CardEmptyHint extends StatelessWidget {
  const CardEmptyHint(this.message, {super.key});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: ZentwikTokens.spaceSm),
      child: Text(
        message,
        style: TextStyle(
          fontSize: ZentwikTokens.fontBody,
          color: context.palette.textTertiary,
        ),
      ),
    );
  }
}
