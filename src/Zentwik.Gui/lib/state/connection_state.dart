import 'package:flutter/foundation.dart';

import '../protocol/protocol.dart';

enum ConnectionPhase {
  disconnected,
  connecting,
  connected,
  error,
}

/// 管理与后台服务的连接生命周期以及基础状态（服务状态、能力、模块、设置）。
class AgentConnectionState extends ChangeNotifier {
  AgentConnectionState(this._client);

  final ProtocolClient _client;

  ConnectionPhase _phase = ConnectionPhase.disconnected;
  AgentStatusDto? _status;
  List<CapabilityDto> _capabilities = const [];
  List<ModuleDto> _modules = const [];
  SettingsDto? _settings;
  String? _errorMessage;

  ConnectionPhase get phase => _phase;
  AgentStatusDto? get status => _status;
  List<CapabilityDto> get capabilities => _capabilities;
  List<ModuleDto> get modules => _modules;
  SettingsDto? get settings => _settings;
  String? get errorMessage => _errorMessage;
  bool get isConnected => _phase == ConnectionPhase.connected;

  Future<void> connect() async {
    if (_phase == ConnectionPhase.connecting ||
        _phase == ConnectionPhase.connected) {
      return;
    }

    _phase = ConnectionPhase.connecting;
    _errorMessage = null;
    notifyListeners();

    try {
      await _client.connect();
      _phase = ConnectionPhase.connected;
      notifyListeners();
      await refreshAll();
    } on ProtocolException catch (error) {
      _phase = ConnectionPhase.error;
      _errorMessage = error.userMessage;
      notifyListeners();
    } catch (error) {
      _phase = ConnectionPhase.error;
      _errorMessage = error.toString();
      notifyListeners();
    }
  }

  Future<void> refreshAll() async {
    if (!_client.isConnected) {
      return;
    }

    try {
      final results = await Future.wait([
        _client.getStatus(),
        _client.listCapabilities(),
        _client.listModules(),
        _client.getSettings(),
      ]);

      _status = results[0] as AgentStatusDto;
      _capabilities = results[1] as List<CapabilityDto>;
      _modules = results[2] as List<ModuleDto>;
      _settings = results[3] as SettingsDto;
      _errorMessage = null;
      notifyListeners();
    } on ProtocolException catch (error) {
      if (error.code == ErrorCodes.connectionClosed) {
        _phase = ConnectionPhase.disconnected;
      } else {
        _errorMessage = error.userMessage;
      }
      notifyListeners();
    }
  }

  Future<void> updateSettings(SettingsPatch patch) async {
    try {
      _settings = await _client.setSettings(patch);
      _errorMessage = null;
      notifyListeners();
    } on ProtocolException catch (error) {
      _errorMessage = error.userMessage;
      notifyListeners();
    }
  }

  Future<void> setModuleEnabled(String id, {required bool enabled}) async {
    try {
      _modules = enabled
          ? await _client.enableModule(id)
          : await _client.disableModule(id);
      _errorMessage = null;
      notifyListeners();
    } on ProtocolException catch (error) {
      _errorMessage = error.userMessage;
      notifyListeners();
    }
  }

  Future<void> disconnect() async {
    await _client.close();
    _phase = ConnectionPhase.disconnected;
    notifyListeners();
  }

  @override
  void dispose() {
    _client.close();
    super.dispose();
  }
}
