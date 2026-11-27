import 'json_helpers.dart';

class SubscriptionAck {
  const SubscriptionAck({
    this.intervalMs,
    this.sensorIntervalMs,
  });

  factory SubscriptionAck.fromJson(Map<String, Object?> json) {
    return SubscriptionAck(
      intervalMs: jsonInt(json['intervalMs']),
      sensorIntervalMs: jsonInt(json['sensorIntervalMs']),
    );
  }

  final int? intervalMs;
  final int? sensorIntervalMs;
}

class CoreClockDto {
  const CoreClockDto({
    this.ccd,
    this.core,
    this.megahertz,
  });

  factory CoreClockDto.fromJson(Map<String, Object?> json) {
    return CoreClockDto(
      ccd: jsonInt(json['ccd']),
      core: jsonInt(json['core']),
      megahertz: jsonDouble(json['megahertz']),
    );
  }

  final int? ccd;
  final int? core;
  final double? megahertz;
}

class TelemetryDto {
  const TelemetryDto({
    this.timestamp,
    this.cpuTotalPercent,
    this.memoryTotalBytes,
    this.memoryAvailableBytes,
    this.processCount,
    this.processorCount,
    this.temperatureCelsius,
    this.packagePowerWatts,
    this.sensorDetail,
    this.coreClocks = const [],
    this.processorUtilization,
  });

  factory TelemetryDto.fromJson(Map<String, Object?> json) {
    final rawClocks = json['coreClocks'];
    final clocks = rawClocks is List
        ? rawClocks
            .whereType<Map<String, Object?>>()
            .map(CoreClockDto.fromJson)
            .toList(growable: false)
        : const <CoreClockDto>[];

    return TelemetryDto(
      timestamp: jsonDateTime(json['timestamp']),
      cpuTotalPercent: jsonDouble(json['cpuTotalPercent']),
      memoryTotalBytes: jsonInt(json['memoryTotalBytes']),
      memoryAvailableBytes: jsonInt(json['memoryAvailableBytes']),
      processCount: jsonInt(json['processCount']),
      processorCount: jsonInt(json['processorCount']),
      temperatureCelsius: jsonDouble(json['temperatureCelsius']),
      packagePowerWatts: jsonDouble(json['packagePowerWatts']),
      sensorDetail: jsonString(json['sensorDetail']),
      coreClocks: clocks,
      processorUtilization: jsonDoubleList(json['processorUtilization']),
    );
  }

  final DateTime? timestamp;
  final double? cpuTotalPercent;
  final int? memoryTotalBytes;
  final int? memoryAvailableBytes;
  final int? processCount;
  final int? processorCount;
  final double? temperatureCelsius;
  final double? packagePowerWatts;
  final String? sensorDetail;
  final List<CoreClockDto> coreClocks;
  final List<double>? processorUtilization;
}

class HistoryDto {
  const HistoryDto({
    this.capacity,
    this.count,
    this.samples = const [],
  });

  factory HistoryDto.fromJson(Map<String, Object?> json) {
    final rawSamples = json['samples'];
    final parsedSamples = rawSamples is List
        ? rawSamples
            .whereType<Map<String, Object?>>()
            .map(TelemetryDto.fromJson)
            .toList(growable: false)
        : const <TelemetryDto>[];

    return HistoryDto(
      capacity: jsonInt(json['capacity']),
      count: jsonInt(json['count']),
      samples: parsedSamples,
    );
  }

  final int? capacity;
  final int? count;
  final List<TelemetryDto> samples;
}
