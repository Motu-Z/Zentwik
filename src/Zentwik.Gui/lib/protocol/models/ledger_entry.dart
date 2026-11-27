import '../errors.dart';
import 'json_helpers.dart';

class LedgerEntry {
  const LedgerEntry({
    this.target,
    this.planId,
    this.previousValue,
    this.desiredValue,
    this.actualValue,
    this.source,
    this.owner,
    this.priority,
    this.status,
    this.updatedAt,
    this.restorable,
    this.processStartTicks,
  });

  factory LedgerEntry.fromJson(Map<String, Object?> json) {
    return LedgerEntry(
      target: jsonString(json['target']),
      planId: jsonString(json['planId']),
      previousValue: jsonString(json['previousValue']),
      desiredValue: jsonString(json['desiredValue']),
      actualValue: jsonString(json['actualValue']),
      source: jsonString(json['source']),
      owner: jsonInt(json['owner']),
      priority: jsonInt(json['priority']),
      status: jsonInt(json['status']),
      updatedAt: jsonDateTime(json['updatedAt']),
      restorable: jsonBool(json['restorable']),
      processStartTicks: jsonInt(json['processStartTicks']),
    );
  }

  final String? target;
  final String? planId;
  final String? previousValue;
  final String? desiredValue;
  final String? actualValue;
  final String? source;
  final int? owner;
  final int? priority;
  final int? status;
  final DateTime? updatedAt;
  final bool? restorable;
  final int? processStartTicks;

  LedgerOwner? get ownerEnum => LedgerOwner.fromWire(owner);
  LedgerStatus? get statusEnum => LedgerStatus.fromWire(status);
}
