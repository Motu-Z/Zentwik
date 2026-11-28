import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/settings_dto.dart';
import '../theme/tokens.dart';

/// 设置页：支持遥测间隔、语言、主题、强调色与界面缩放编辑。
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
  static const double minUiScale = 0.8;
  static const double maxUiScale = 1.6;
  static const int uiScaleDivisions = 16;

  static const Map<String, String> languageOptions = {
    'zh-CN': '简体中文',
    'zh-TW': '繁體中文',
    'en-US': 'English',
  };

  static const Map<String, String> themeOptions = {
    'dark': '暗色',
    'light': '亮色',
  };

  static const Map<String, String> accentOptions = {
    'blue': '蓝色',
    'violet': '紫色',
    'teal': '青色',
    'amber': '琥珀色',
  };

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
  int? _sentMonitor;
  int? _sentSensor;
  double? _draggingScale;
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
    if (widget.settings?.uiScale != oldWidget.settings?.uiScale) {
      _draggingScale = null;
    }
  }

  @override
  void dispose() {
    _flushPendingEdits();
    _monitorFocus.dispose();
    _sensorFocus.dispose();
    _monitorController.dispose();
    _sensorController.dispose();
    super.dispose();
  }

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
      _monitorController.text =
          widget.settings?.monitorIntervalMs?.toString() ?? '';
      _setNotice('遥测推送间隔需为 $min–$max 之间的整数，已恢复为原值。');
      return;
    }

    final clamped = parsed.clamp(min, max).toInt();
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
      _sensorController.text =
          widget.settings?.sensorIntervalMs?.toString() ?? '';
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

    final languageValue =
        SettingsPage.languageOptions.containsKey(current.language)
            ? current.language
            : null;
    final themeValue = SettingsPage.themeOptions.containsKey(current.theme)
        ? current.theme
        : null;
    final accentValue = SettingsPage.accentOptions.containsKey(current.accent)
        ? current.accent
        : null;
    final rawScale = _draggingScale ?? current.uiScale ?? 1.0;
    final sliderValue = rawScale
        .clamp(SettingsPage.minUiScale, SettingsPage.maxUiScale)
        .toDouble();

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
          _EnumDropdownRow(
            label: '语言',
            value: languageValue,
            options: SettingsPage.languageOptions,
            enabled: !widget.readOnly,
            onChanged: (value) {
              if (value != null) {
                widget.onChange?.call(SettingsPatch(language: value));
              }
            },
          ),
          const SizedBox(height: ZentwikTokens.spaceMd),
          _EnumDropdownRow(
            label: '主题',
            value: themeValue,
            options: SettingsPage.themeOptions,
            enabled: !widget.readOnly,
            onChanged: (value) {
              if (value != null) {
                widget.onChange?.call(SettingsPatch(theme: value));
              }
            },
          ),
          const SizedBox(height: ZentwikTokens.spaceMd),
          _EnumDropdownRow(
            label: '强调色',
            value: accentValue,
            options: SettingsPage.accentOptions,
            enabled: !widget.readOnly,
            onChanged: (value) {
              if (value != null) {
                widget.onChange?.call(SettingsPatch(accent: value));
              }
            },
          ),
          const SizedBox(height: ZentwikTokens.spaceMd),
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              const Text(
                '界面缩放',
                style: TextStyle(
                  fontSize: ZentwikTokens.fontBody,
                  color: ZentwikTokens.textSecondary,
                ),
              ),
              Text(
                current.uiScale == null && _draggingScale == null
                    ? kMissingValue
                    : sliderValue.toStringAsFixed(2),
                style: const TextStyle(
                  fontSize: ZentwikTokens.fontBody,
                  color: ZentwikTokens.textPrimary,
                ),
              ),
            ],
          ),
          Slider(
            value: sliderValue,
            min: SettingsPage.minUiScale,
            max: SettingsPage.maxUiScale,
            divisions: SettingsPage.uiScaleDivisions,
            label: sliderValue.toStringAsFixed(2),
            onChanged: widget.readOnly
                ? null
                : (value) => setState(() => _draggingScale = value),
            onChangeEnd: widget.readOnly
                ? null
                : (value) {
                    final rounded =
                        double.parse(value.toStringAsFixed(2));
                    setState(() => _draggingScale = null);
                    widget.onChange?.call(SettingsPatch(uiScale: rounded));
                  },
          ),
          const SizedBox(height: ZentwikTokens.spaceSm),
          // 说实话比好看重要：这四项确实只写进了配置，界面渲染还没接上。
          // 不说清楚，用户点「亮色」发现没反应，只会认为软件坏了。
          const Text(
            '语言、主题、强调色与界面缩放会写入配置，但界面渲染尚未接入——'
            '目前改动不会立刻改变外观。上面两个推送间隔是即时生效的。',
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: ZentwikTokens.textTertiary,
            ),
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

class _EnumDropdownRow extends StatelessWidget {
  const _EnumDropdownRow({
    required this.label,
    required this.value,
    required this.options,
    required this.enabled,
    required this.onChanged,
  });

  final String label;
  final String? value;
  final Map<String, String> options;
  final bool enabled;
  final ValueChanged<String?> onChanged;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(
          label,
          style: const TextStyle(
            fontSize: ZentwikTokens.fontBody,
            color: ZentwikTokens.textSecondary,
          ),
        ),
        DropdownButton<String>(
          value: value,
          hint: const Text(kMissingValue),
          items: [
            for (final entry in options.entries)
              DropdownMenuItem<String>(
                value: entry.key,
                child: Text(entry.value),
              ),
          ],
          onChanged: enabled ? onChanged : null,
        ),
      ],
    );
  }
}
