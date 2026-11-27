/// 协议错误码常量（对应契约 §4，外加客户端断线错误码 `connection.closed`）。
abstract final class ErrorCodes {
  static const String protocolVersionMismatch = 'protocol.version_mismatch';
  static const String protocolMalformed = 'protocol.malformed';
  static const String unknownOperation = 'request.unknown_operation';
  static const String payloadInvalid = 'request.payload_invalid';
  static const String unauthorized = 'request.unauthorized';
  static const String internal = 'agent.internal';

  static const String planNotFound = 'plan.not_found';
  static const String planInvalid = 'plan.invalid';
  static const String sceneNotFound = 'scene.not_found';
  static const String sceneInvalid = 'scene.invalid';
  static const String ledgerEntryMissing = 'ledger.entry_missing';

  static const String hardwareUnavailable = 'hardware.unavailable';
  static const String hardwareWriteFailed = 'hardware.write_failed';
  static const String hardwareVerifyFailed = 'hardware.verify_failed';

  static const String powerSchemeNotFound = 'power.scheme_not_found';
  static const String powerSchemeBuiltIn = 'power.scheme_builtin';
  static const String powerSchemeNameConflict = 'power.scheme_name_conflict';
  static const String powerSchemeCreateFailed = 'power.scheme_create_failed';
  static const String powerSchemeDeleteFailed = 'power.scheme_delete_failed';
  static const String powerSchemeBackupFailed = 'power.scheme_backup_failed';
  static const String powerSchemeBackupNotFound = 'power.scheme_backup_not_found';
  static const String powerSchemeRestoreFailed = 'power.scheme_restore_failed';

  static const String moduleNotFound = 'module.not_found';
  static const String moduleNotOptional = 'module.not_optional';
  static const String moduleDisabled = 'module.disabled';

  static const String powerLimitRejected = 'power.limit_rejected';
  static const String powerLimitInvalid = 'power.limit_invalid';
  static const String powerLimitUnsupported = 'power.limit_unsupported';

  /// 客户端在读循环检测到对端关闭时抛出的错误码。
  static const String connectionClosed = 'connection.closed';
}

/// 协议请求失败或连接中断时抛出的异常。
class ProtocolException implements Exception {
  const ProtocolException(
    this.code, {
    this.detail,
    this.op,
  });

  final String code;
  final String? detail;
  final String? op;

  /// 面向界面的中文提示文案。
  String get userMessage => describe(code);

  @override
  String toString() {
    final buffer = StringBuffer('ProtocolException($code');
    if (op != null) {
      buffer.write(', op: $op');
    }
    if (detail != null && detail!.isNotEmpty) {
      buffer.write(', detail: $detail');
    }
    buffer.write(')');
    return buffer.toString();
  }
}

/// 将协议错误码映射为面向界面直接显示的中文文案，不包含英文错误码原串。
String describe(String code) {
  return switch (code) {
    ErrorCodes.protocolVersionMismatch => '客户端与服务端协议版本不匹配，请更新后重试。',
    ErrorCodes.protocolMalformed => '请求报文格式有误，无法解析。',
    ErrorCodes.unknownOperation => '服务端不支持该操作。',
    ErrorCodes.payloadInvalid => '请求参数无效，请检查输入内容。',
    ErrorCodes.unauthorized => '权限不足，需要以管理员身份运行后重试。',
    ErrorCodes.internal => '服务端内部发生错误，请查看诊断日志。',
    ErrorCodes.planNotFound => '未找到指定的优化方案。',
    ErrorCodes.planInvalid => '优化方案配置无效，请检查参数。',
    ErrorCodes.sceneNotFound => '未找到指定的场景。',
    ErrorCodes.sceneInvalid => '场景配置无效，请检查触发条件与动作。',
    ErrorCodes.ledgerEntryMissing => '未找到对应的账本记录。',
    ErrorCodes.hardwareUnavailable => '当前硬件控制能力不可用。',
    ErrorCodes.hardwareWriteFailed => '硬件参数写入失败，请勿重复提交，检查硬件状态后重试。',
    ErrorCodes.hardwareVerifyFailed => '硬件参数写入后回读校验未通过。',
    ErrorCodes.powerSchemeNotFound => '未找到指定的电源方案。',
    ErrorCodes.powerSchemeBuiltIn => '系统内置电源方案不允许执行此操作。',
    ErrorCodes.powerSchemeNameConflict => '电源方案名称已存在，请更换名称。',
    ErrorCodes.powerSchemeCreateFailed => '创建电源方案失败。',
    ErrorCodes.powerSchemeDeleteFailed => '删除电源方案失败。',
    ErrorCodes.powerSchemeBackupFailed => '备份电源方案失败。',
    ErrorCodes.powerSchemeBackupNotFound => '未找到指定的电源方案备份。',
    ErrorCodes.powerSchemeRestoreFailed => '恢复电源方案失败。',
    ErrorCodes.moduleNotFound => '未找到指定的功能模块。',
    ErrorCodes.moduleNotOptional => '该核心模块为必选模块，无法禁用。',
    ErrorCodes.moduleDisabled => '该功能模块当前已禁用，请先在模块管理中启用。',
    ErrorCodes.powerLimitRejected => '功耗墙设置请求被拒绝。',
    ErrorCodes.powerLimitInvalid => '功耗墙参数超出有效范围。',
    ErrorCodes.powerLimitUnsupported => '当前平台或硬件不支持功耗墙调节。',
    ErrorCodes.connectionClosed => '与后台服务的连接已断开。',
    _ => '操作失败，请稍后重试。',
  };
}

/// `LedgerEntry.owner` 枚举映射（契约 §5）。
enum LedgerOwner {
  none(0),
  scene(1),
  user(2),
  plan(3),
  system(4);

  const LedgerOwner(this.wire);

  final int wire;

  static LedgerOwner? fromWire(int? value) {
    if (value == null) return null;
    for (final item in values) {
      if (item.wire == value) return item;
    }
    return null;
  }
}

/// `LedgerEntry.status` 枚举映射（契约 §5）。
///
/// 注意：`unknown`（4）表示“写了但读不回来”，不得当作成功或失败。
enum LedgerStatus {
  applied(0),
  reverted(1),
  failed(2),
  skipped(3),
  unknown(4);

  const LedgerStatus(this.wire);

  final int wire;

  static LedgerStatus? fromWire(int? value) {
    if (value == null) return null;
    for (final item in values) {
      if (item.wire == value) return item;
    }
    return null;
  }
}

/// `Plan.kind` 枚举映射（契约 §5）。
enum PlanKind {
  cpuTuning(0),
  power(1),
  scheduling(2);

  const PlanKind(this.wire);

  final int wire;

  static PlanKind? fromWire(int? value) {
    if (value == null) return null;
    for (final item in values) {
      if (item.wire == value) return item;
    }
    return null;
  }
}

/// `lastApply.state` 枚举映射（契约 §5）。
enum LastApplyState {
  notApplied(0),
  applied(1),
  partial(2),
  failed(3);

  const LastApplyState(this.wire);

  final int wire;

  static LastApplyState? fromWire(int? value) {
    if (value == null) return null;
    for (final item in values) {
      if (item.wire == value) return item;
    }
    return null;
  }
}

/// `CapabilityDto.state` 字符串常量（契约 §5）。
abstract final class CapabilityStates {
  static const String unavailable = 'unavailable';
  static const String installed = 'installed';
  static const String enabled = 'enabled';
  static const String loaded = 'loaded';
  static const String running = 'running';
  static const String degraded = 'degraded';
  static const String failed = 'failed';
}

/// `ModuleDto.state` 字符串常量（契约 §5）。
abstract final class ModuleStates {
  static const String ready = 'ready';
  static const String degraded = 'degraded';
  static const String unavailable = 'unavailable';
}
