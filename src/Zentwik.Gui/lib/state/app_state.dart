import 'dart:async';

import 'package:flutter/foundation.dart';

import '../protocol/protocol.dart';

/// 遥测事件本地缓存上限（保留最近 60 帧）。
const int kAppStateTelemetryCapacity = 60;

/// 聚合服务状态、能力列表、模块列表与遥测流缓存，持有当前生效的 [ProtocolClient]。
///
/// 连接层每次重连都会新建客户端，[attach] 负责把聚合状态切到新客户端上；
/// 否则重连成功后界面会一直读旧连接的数据。
class AppState extends ChangeNotifier {
  AppState(ProtocolClient client) {
    _client = client;
    _telemetrySub = client.telemetry.listen(_onTelemetry);
  }

  ProtocolClient? _client;
  final List<TelemetryDto> _telemetryHistory = [];

  StreamSubscription<TelemetryDto>? _telemetrySub;
  Future<void>? _refreshFuture;
  bool _disposed = false;

  AgentStatusDto? _status;
  List<CapabilityDto> _capabilities = const [];
  List<ModuleDto> _modules = const [];
  SettingsDto? _settings;
  List<LedgerEntry> _ledger = const [];
  String? _busyModuleId;
  ProtocolException? _lastError;
  String? _lastErrorMessage;

  AgentStatusDto? get status => _status;
  List<CapabilityDto> get capabilities => _capabilities;
  List<ModuleDto> get modules => _modules;
  SettingsDto? get settings => _settings;
  List<LedgerEntry> get ledger => _ledger;

  /// 正在等待后端回应的模块 id；界面据此显示行内进度并禁止重复点击。
  String? get busyModuleId => _busyModuleId;
  TelemetryDto? get telemetry =>
      _telemetryHistory.isEmpty ? null : _telemetryHistory.last;
  List<TelemetryDto> get telemetryHistory =>
      List.unmodifiable(_telemetryHistory);
  ProtocolException? get lastError => _lastError;

  /// 非协议层错误（超时、客户端已关闭等）的文本描述，与 [lastError] 互补。
  String? get lastErrorMessage => _lastErrorMessage;

  /// 面向界面的一句话错误提示。
  ///
  /// 优先用后端 `detail`（例如「模块 monitoring 是内置组件，始终启用」），
  /// 它比错误码文案具体；没有 detail 才退回错误码映射。
  String? get errorMessage {
    final protocol = _lastError;
    if (protocol != null) {
      final detail = protocol.detail;
      if (detail != null && detail.trim().isNotEmpty) {
        return detail;
      }
      return protocol.userMessage;
    }
    return _lastErrorMessage;
  }

  /// 切到新的客户端并立刻拉一轮快照。
  ///
  /// 只换订阅与引用，不清空已有缓存：短暂断线时界面保留上一帧数据，
  /// 用 `lastError` / 连接状态表达「这份数据可能不新鲜」。
  void attach(ProtocolClient client) {
    if (_disposed) {
      return;
    }
    _telemetrySub?.cancel();
    _client = client;
    _telemetrySub = client.telemetry.listen(_onTelemetry);
    unawaited(refresh());
  }

  /// 并发刷新状态、能力、模块、设置与账本快照。
  ///
  /// 单个请求失败不影响其余项更新，失败项保留旧缓存并记录 `lastError`；
  /// 并发多次调用去重，同一时刻只跑一轮。
  Future<void> refresh() {
    return _refreshFuture ??= _refreshInternal();
  }

  Future<void> _refreshInternal() async {
    final client = _client;
    if (client == null) {
      return;
    }
    ProtocolException? roundError;
    String? roundErrorMessage;

    void capture(Object error) {
      if (error is ProtocolException) {
        roundError = error;
      } else {
        roundErrorMessage = error.toString();
      }
    }

    try {
      await Future.wait([
        client.getStatus().then((value) {
          _status = value;
        }).catchError(capture),
        client.listCapabilities().then((value) {
          _capabilities = value;
        }).catchError(capture),
        client.listModules().then((value) {
          _modules = value;
        }).catchError(capture),
        client.getSettings().then((value) {
          _settings = value;
        }).catchError(capture),
        client.listLedger().then((value) {
          _ledger = value;
        }).catchError(capture),
      ]);

      _lastError = roundError;
      _lastErrorMessage = roundErrorMessage;
      _notify();
    } finally {
      _refreshFuture = null;
    }
  }

  /// 启停可选模块，并用后端回传的完整模块列表替换本地缓存。
  ///
  /// 只有 `optional == true` 的模块能被启停：后端对内置模块直接回
  /// `module_not_optional`，所以调用方要自己判 `optional`，别把错误当网络故障。
  /// 同一时刻只允许一个模块切换，[busyModuleId] 表达进行中的那一个。
  Future<bool> setModuleEnabled(String id, bool enable) async {
    final client = _client;
    if (_disposed || client == null || _busyModuleId != null) {
      return false;
    }

    _busyModuleId = id;
    _notify();

    try {
      final modules = enable
          ? await client.enableModule(id)
          : await client.disableModule(id);
      _modules = modules;
      _lastError = null;
      _lastErrorMessage = null;
      return true;
    } catch (error) {
      _captureError(error);
      return false;
    } finally {
      _busyModuleId = null;
      _notify();
    }
  }

  /// 写入设置增量，并用后端回传的整份设置替换本地缓存。
  Future<bool> saveSettings(SettingsPatch patch) async {
    final client = _client;
    if (_disposed || client == null) {
      return false;
    }

    try {
      _settings = await client.setSettings(patch);
      _lastError = null;
      _lastErrorMessage = null;
      _notify();
      return true;
    } catch (error) {
      _captureError(error);
      _notify();
      return false;
    }
  }

  void _captureError(Object error) {
    if (error is ProtocolException) {
      _lastError = error;
      _lastErrorMessage = null;
    } else {
      _lastErrorMessage = error.toString();
    }
  }

  void _onTelemetry(TelemetryDto sample) {
    if (_disposed) {
      return;
    }
    _telemetryHistory.add(sample);
    if (_telemetryHistory.length > kAppStateTelemetryCapacity) {
      _telemetryHistory.removeRange(
        0,
        _telemetryHistory.length - kAppStateTelemetryCapacity,
      );
    }
    _notify();
  }

  void _notify() {
    if (_disposed) {
      return;
    }
    notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _telemetrySub?.cancel();
    _telemetrySub = null;
    super.dispose();
  }
}
