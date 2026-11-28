import 'package:material_ui/material_ui.dart';

import 'runtime/agent_runtime.dart';
import 'runtime/app_host.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(ZentwikRoot(runtime: AgentRuntime()));
}
