import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/format/units.dart';

void main() {
  group('units formatting', () {
    test('formatPercent 处理 null、NaN、无穷、负值与正常值', () {
      expect(formatPercent(null), kMissingValue);
      expect(formatPercent(double.nan), kMissingValue);
      expect(formatPercent(double.infinity), kMissingValue);
      expect(formatPercent(-0.1), kMissingValue);
      expect(formatPercent(0), '0.0%');
      expect(formatPercent(12.34), '12.3%');
    });

    test('formatCelsius 处理 null、NaN、负值与正常值', () {
      expect(formatCelsius(null), kMissingValue);
      expect(formatCelsius(double.nan), kMissingValue);
      expect(formatCelsius(double.negativeInfinity), kMissingValue);
      expect(formatCelsius(-5.0), '-5.0 °C');
      expect(formatCelsius(64.52), '64.5 °C');
    });

    test('formatWatts 处理 null、NaN、负值与正常值', () {
      expect(formatWatts(null), kMissingValue);
      expect(formatWatts(double.nan), kMissingValue);
      expect(formatWatts(-1.0), kMissingValue);
      expect(formatWatts(35.24), '35.2 W');
    });

    test('formatGigahertz 处理 null、NaN、负值与正常值', () {
      expect(formatGigahertz(null), kMissingValue);
      expect(formatGigahertz(double.nan), kMissingValue);
      expect(formatGigahertz(-100), kMissingValue);
      expect(formatGigahertz(4450), '4.45 GHz');
    });

    test('formatBytes 处理 null、负值、字节与 KB/MB/GB/TB 进位', () {
      expect(formatBytes(null), kMissingValue);
      expect(formatBytes(-1), kMissingValue);
      expect(formatBytes(512), '512 B');
      expect(formatBytes(1536), '1.5 KB');
      expect(formatBytes(16750372454), '15.6 GB');
    });

    test('formatCount 处理 null、负值与正常值', () {
      expect(formatCount(null), kMissingValue);
      expect(formatCount(-1), kMissingValue);
      expect(formatCount(0), '0');
      expect(formatCount(312), '312');
    });

    test('formatUptime 按天/时/分折算并省略前导零段', () {
      expect(formatUptime(null), kMissingValue);
      expect(formatUptime(-1), kMissingValue);
      expect(formatUptime(0), '0 秒');
      expect(formatUptime(59), '59 秒');
      expect(formatUptime(60), '1 分');
      expect(formatUptime(6879), '1 小时 54 分');
      expect(formatUptime(86400), '1 天 0 小时');
      expect(formatUptime(90061), '1 天 1 小时');
    });
  });
}
