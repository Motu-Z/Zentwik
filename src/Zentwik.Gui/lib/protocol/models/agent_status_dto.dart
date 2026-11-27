import 'json_helpers.dart';

class AgentStatusDto {
  const AgentStatusDto({
    this.version,
    this.protocolVersion,
    this.machineName,
    this.elevated,
    this.runningAsService,
    this.simulated,
    this.processorCount,
    this.physicalCoreCount,
    this.processorGroups,
    this.startedAt,
    this.uptimeSeconds,
    this.dataDirectory,
    this.activeSceneId,
    this.planCount,
    this.sceneCount,
    this.ledgerEntryCount,
  });

  factory AgentStatusDto.fromJson(Map<String, Object?> json) {
    return AgentStatusDto(
      version: jsonString(json['version']),
      protocolVersion: jsonInt(json['protocolVersion']),
      machineName: jsonString(json['machineName']),
      elevated: jsonBool(json['elevated']),
      runningAsService: jsonBool(json['runningAsService']),
      simulated: jsonBool(json['simulated']),
      processorCount: jsonInt(json['processorCount']),
      physicalCoreCount: jsonInt(json['physicalCoreCount']),
      processorGroups: jsonInt(json['processorGroups']),
      startedAt: jsonDateTime(json['startedAt']),
      uptimeSeconds: jsonInt(json['uptimeSeconds']),
      dataDirectory: jsonString(json['dataDirectory']),
      activeSceneId: jsonString(json['activeSceneId']),
      planCount: jsonInt(json['planCount']),
      sceneCount: jsonInt(json['sceneCount']),
      ledgerEntryCount: jsonInt(json['ledgerEntryCount']),
    );
  }

  final String? version;
  final int? protocolVersion;
  final String? machineName;
  final bool? elevated;
  final bool? runningAsService;
  final bool? simulated;
  final int? processorCount;
  final int? physicalCoreCount;
  final int? processorGroups;
  final DateTime? startedAt;
  final int? uptimeSeconds;
  final String? dataDirectory;
  final String? activeSceneId;
  final int? planCount;
  final int? sceneCount;
  final int? ledgerEntryCount;
}
