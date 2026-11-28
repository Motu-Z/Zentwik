import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/settings_dto.dart';
import '../theme/tokens.dart';

/// 设置页：展示 `SettingsDto` 全部已有字段并对遥测间隔做范围夹取。
class SettingsPage extends StatefulWidget {
  const SettingsPage({
    super.key,
    this.settings,
    this.onChange,
    this.readOnly = false,
  });

  static const int minMonitorIntervalMs = 500;
  static const int maxMonitorIntervalMs = 5000;
  static const int minSensorIntervalMs = 500;
  static const int maxSensorIntervalMs = 30000;

  final SettingsDto? settings;
  final ValueChanged<SettingsPatch>? onChange;
  final bool readOnly;

  @override
  State<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends State<SettingsPage> {
  late final TextEditingController _monitorController;
  late final TextEditingController _sensorController;
  String? _clampNotice;

  @override
  void initState() {
    super.initState();
    _monitorController = TextEditingController(
      text: widget.settings?.monitorIntervalMs?.toString() ?? '',
    );
    _sensorController = TextEditingController(
      text: widget.settings?.sensorIntervalMs?.toString() ?? '',
    );
  }

  @override
  void didUpdateWidget(SettingsPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.settings?.monitorIntervalMs !=
        oldWidget.settings?.monitorIntervalMs) {
      _monitorController.text =
          widget.settings?.monitorIntervalMs?.toString() ?? '';
    }
    if (widget.settings?.sensorIntervalMs !=
        oldWidget.settings?.sensorIntervalMs) {
      _sensorController.text =
          widget.settings?.sensorIntervalMs?.toString() ?? '';
    }
  }

  @override
  void dispose() {
    _monitorController.dispose();
    _sensorController.dispose();
    super.dispose();
  }

  void _submitMonitorInterval(String raw) {
    final parsed = int.tryParse(raw.trim());
    if (parsed == null) {
      return;
    }
    final clamped = parsed
        .clamp(
          SettingsPage.minMonitorIntervalMs,
          SettingsPage.maxMonitorIntervalMs,
        )
        .toInt();

    if (clamped != parsed) {
      _monitorController.text = '$clamped';
      setState(() {
        _clampNotice = '遥测推送间隔允许范围为 '
            '${SettingsPage.minMonitorIntervalMs}–'
            '${SettingsPage.maxMonitorIntervalMs} ms，已调整为 $clamped ms。';
      });
    } else if (_clampNotice != null) {
      setState(() => _clampNotice = null);
    }

    widget.onChange?.call(SettingsPatch(monitorIntervalMs: clamped));
  }

  void _submitSensorInterval(String raw) {
    final parsed = int.tryParse(raw.trim());
    if (parsed == null) {
      return;
    }
    final clamped = parsed
        .clamp(
          SettingsPage.minSensorIntervalMs,
          SettingsPage.maxSensorIntervalMs,
        )
        .toInt();

    if (clamped != parsed) {
      _sensorController.text = '$clamped';
      setState(() {
        _clampNotice = 'CPU 传感器间隔允许范围为 '
            '${SettingsPage.minSensorIntervalMs}–'
            '${SettingsPage.maxSensorIntervalMs} ms，已调整为 $clamped ms。';
      });
    } else if (_clampNotice != null) {
      setState(() => _clampNotice = null);
    }

    widget.onChange?.call(SettingsPatch(sensorIntervalMs: clamped));
  }

  @override
  Widget build(BuildContext context) {
    final current = widget.settings;
    if (current == null) {
      return const Center(
        child: Text(
          kMissingValue,
          style: TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            color: ZentwikTokens.textSecondary,
          ),
        ),
      );
    }

    return SingleChildScrollView(
      padding: const EdgeInsets.all(ZentwikTokens.spaceLg),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text(
            '设置',
            style: TextStyle(
              fontSize: ZentwikTokens.fontTitle,
              fontWeight: FontWeight.w600,
              color: ZentwikTokens.textPrimary,
            ),
          ),
          const SizedBox(height: ZentwikTokens.spaceLg),
          _IntervalField(
            label: '遥测推送间隔（500–5000 ms）',
            controller: _monitorController,
            enabled: !widget.readOnly,
            onSubmitted: _submitMonitorInterval,
          ),
          const SizedBox(height: ZentwikTokens.spaceMd),
          _IntervalField(
            label: 'CPU 传感器间隔（500–30000 ms）',
            controller: _sensorController,
            enabled: !widget.readOnly,
            onSubmitted: _submitSensorInterval,
          ),
          if (_clampNotice != null) ...[
            const SizedBox(height: ZentwikTokens.spaceSm),
            Text(
              _clampNotice!,
              style: const TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                color: ZentwikTokens.accent,
              ),
            ),
          ],
          const SizedBox(height: ZentwikTokens.spaceXl),
          _ReadOnlyRow(
            label: '语言（language）',
            value: current.language ?? kMissingValue,
          ),
          _ReadOnlyRow(
            label: '主题（theme）',
            value: current.theme ?? kMissingValue,
          ),
          _ReadOnlyRow(
            label: '强调色（accent）',
            value: current.accent ?? kMissingValue,
          ),
          _ReadOnlyRow(
            label: '界面缩放（uiScale）',
            value: current.uiScale?.toString() ?? kMissingValue,
          ),
        ],
      ),
    );
  }
}

class _IntervalField extends StatelessWidget {
  const _IntervalField({
    required this.label,
    required this.controller,
    required this.enabled,
    required this.onSubmitted,
  });

  final String label;
  final TextEditingController controller;
  final bool enabled;
  final ValueChanged<String> onSubmitted;

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: controller,
      enabled: enabled,
      keyboardType: TextInputType.number,
      onSubmitted: onSubmitted,
      decoration: InputDecoration(
        labelText: label,
        suffixText: 'ms',
        border: const OutlineInputBorder(),
      ),
    );
  }
}

class _ReadOnlyRow extends StatelessWidget {
  const _ReadOnlyRow({
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
