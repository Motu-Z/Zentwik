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
  late final FocusNode _monitorFocus;
  late final FocusNode _sensorFocus;
  /// 已经提交出去的间隔值，用来吸收「回车 + 失焦 + 销毁」三路重复触发。
  int? _sentMonitor;
  int? _sentSensor;
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
    _sentMonitor = widget.settings?.monitorIntervalMs;
    _sentSensor = widget.settings?.sensorIntervalMs;
    // 桌面用户输完数字通常是直接点别处，不会按回车：失焦必须提交，
    // 否则改动静默丢失。onSubmitted 只覆盖「按回车」这一条路径。
    _monitorFocus = FocusNode(debugLabel: 'settings.monitorInterval')
      ..addListener(() {
        if (!_monitorFocus.hasFocus && mounted) {
          _submitMonitorInterval(_monitorController.text);
        }
      });
    _sensorFocus = FocusNode(debugLabel: 'settings.sensorInterval')
      ..addListener(() {
        if (!_sensorFocus.hasFocus && mounted) {
          _submitSensorInterval(_sensorController.text);
        }
      });
  }

  @override
  void didUpdateWidget(SettingsPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.settings?.monitorIntervalMs !=
        oldWidget.settings?.monitorIntervalMs) {
      _monitorController.text =
          widget.settings?.monitorIntervalMs?.toString() ?? '';
      _sentMonitor = widget.settings?.monitorIntervalMs;
    }
    if (widget.settings?.sensorIntervalMs !=
        oldWidget.settings?.sensorIntervalMs) {
      _sensorController.text =
          widget.settings?.sensorIntervalMs?.toString() ?? '';
      _sentSensor = widget.settings?.sensorIntervalMs;
    }
  }

  @override
  void dispose() {
    // 切页会直接销毁本 State，此时失焦回调可能来不及跑；在这里补一次。
    _flushPendingEdits();
    _monitorFocus.dispose();
    _sensorFocus.dispose();
    _monitorController.dispose();
    _sensorController.dispose();
    super.dispose();
  }

  /// 把输入框里与服务端不同、且合法的值提交出去；不碰 UI，可在 dispose 里调用。
  void _flushPendingEdits() {
    final monitor = _readInterval(
      _monitorController.text,
      SettingsPage.minMonitorIntervalMs,
      SettingsPage.maxMonitorIntervalMs,
    );
    final sensor = _readInterval(
      _sensorController.text,
      SettingsPage.minSensorIntervalMs,
      SettingsPage.maxSensorIntervalMs,
    );

    final patch = SettingsPatch(
      monitorIntervalMs: monitor != null && monitor != _sentMonitor
          ? monitor
          : null,
      sensorIntervalMs:
          sensor != null && sensor != _sentSensor ? sensor : null,
    );
    if (patch.monitorIntervalMs == null && patch.sensorIntervalMs == null) {
      return;
    }
    _sentMonitor = patch.monitorIntervalMs ?? _sentMonitor;
    _sentSensor = patch.sensorIntervalMs ?? _sentSensor;
    widget.onChange?.call(patch);
  }

  int? _readInterval(String raw, int min, int max) {
    final parsed = int.tryParse(raw.trim());
    if (parsed == null) {
      return null;
    }
    return parsed.clamp(min, max).toInt();
  }

  void _setNotice(String message) {
    if (!mounted || _clampNotice == message) {
      return;
    }
    setState(() => _clampNotice = message);
  }

  void _clearNotice() {
    if (!mounted || _clampNotice == null) {
      return;
    }
    setState(() => _clampNotice = null);
  }

  void _submitMonitorInterval(String raw) {
    const min = SettingsPage.minMonitorIntervalMs;
    const max = SettingsPage.maxMonitorIntervalMs;
    final parsed = int.tryParse(raw.trim());
    if (parsed == null) {
      // 非数字直接退回到服务端已知值，不要把它当 0 或保留在框里骗人。
      _monitorController.text = widget.settings?.monitorIntervalMs?.toString() ?? '';
      _setNotice('遥测推送间隔需为 $min–$max 之间的整数，已恢复为原值。');
      return;
    }

    final clamped = parsed.clamp(min, max).toInt();
    // 回车、失焦、销毁三路都会走到这里；值没变就别重复发请求，
    // 也别把刚才的越界提示当成「用户又改好了」清掉。
    if (clamped == _sentMonitor) {
      return;
    }
    _sentMonitor = clamped;
    if (clamped != parsed) {
      _monitorController.text = '$clamped';
      _setNotice('遥测推送间隔允许范围为 $min–$max ms，已调整为 $clamped ms。');
    } else {
      _clearNotice();
    }
    widget.onChange?.call(SettingsPatch(monitorIntervalMs: clamped));
  }

  void _submitSensorInterval(String raw) {
    const min = SettingsPage.minSensorIntervalMs;
    const max = SettingsPage.maxSensorIntervalMs;
    final parsed = int.tryParse(raw.trim());
    if (parsed == null) {
      _sensorController.text = widget.settings?.sensorIntervalMs?.toString() ?? '';
      _setNotice('CPU 传感器间隔需为 $min–$max 之间的整数，已恢复为原值。');
      return;
    }

    final clamped = parsed.clamp(min, max).toInt();
    if (clamped == _sentSensor) {
      return;
    }
    _sentSensor = clamped;
    if (clamped != parsed) {
      _sensorController.text = '$clamped';
      _setNotice('CPU 传感器间隔允许范围为 $min–$max ms，已调整为 $clamped ms。');
    } else {
      _clearNotice();
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
            focusNode: _monitorFocus,
            enabled: !widget.readOnly,
            onSubmitted: _submitMonitorInterval,
          ),
          const SizedBox(height: ZentwikTokens.spaceMd),
          _IntervalField(
            label: 'CPU 传感器间隔（500–30000 ms）',
            controller: _sensorController,
            focusNode: _sensorFocus,
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
    required this.focusNode,
    required this.enabled,
    required this.onSubmitted,
  });

  final String label;
  final TextEditingController controller;
  final FocusNode focusNode;
  final bool enabled;
  final ValueChanged<String> onSubmitted;

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: controller,
      focusNode: focusNode,
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
