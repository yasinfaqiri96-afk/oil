import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/l10n/app_strings.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_colors.dart';
import '../../../shared/widgets/inline_notice.dart';
import '../../../shared/widgets/status_chip.dart';
import '../application/auth_controller.dart';

class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _username = TextEditingController();
  final _password = TextEditingController();

  bool _obscurePassword = true;
  bool _submitting = false;
  String? _error;

  @override
  void dispose() {
    _username.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (_submitting || !(_formKey.currentState?.validate() ?? false)) return;

    FocusScope.of(context).unfocus();
    setState(() {
      _submitting = true;
      _error = null;
    });

    try {
      await ref.read(authControllerProvider.notifier).login(
            username: _username.text,
            password: _password.text,
          );
      TextInput.finishAutofillContext();
    } on ApiException catch (error) {
      if (!mounted) return;
      _password.clear();
      setState(() => _error = error.message);
    } finally {
      if (mounted) {
        setState(() => _submitting = false);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final config = ref.watch(appConfigProvider);
    final auth = ref.watch(authControllerProvider);
    final signedOutMessage = auth is AuthSignedOut ? auth.message : null;
    final colorScheme = Theme.of(context).colorScheme;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(AppSpacing.xl),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: AutofillGroup(
                child: Form(
                  key: _formKey,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      const _Brand(),
                      const SizedBox(height: AppSpacing.xl),
                      if (!config.isConfigured) ...[
                        const InlineNotice(message: AppStrings.apiNotConfigured, tone: StatusTone.critical),
                        const SizedBox(height: AppSpacing.md),
                      ],
                      if (_error != null || signedOutMessage != null) ...[
                        InlineNotice(
                          message: _error ?? signedOutMessage!,
                          tone: _error != null ? StatusTone.critical : StatusTone.warning,
                        ),
                        const SizedBox(height: AppSpacing.md),
                      ],
                      TextFormField(
                        controller: _username,
                        enabled: !_submitting,
                        textDirection: TextDirection.ltr,
                        textInputAction: TextInputAction.next,
                        autocorrect: false,
                        enableSuggestions: false,
                        autofillHints: const [AutofillHints.username],
                        decoration: const InputDecoration(
                          labelText: AppStrings.username,
                          prefixIcon: Icon(Icons.person_outline_rounded),
                        ),
                        validator: (value) =>
                            (value == null || value.trim().isEmpty) ? AppStrings.usernameRequired : null,
                      ),
                      const SizedBox(height: AppSpacing.md),
                      TextFormField(
                        controller: _password,
                        enabled: !_submitting,
                        obscureText: _obscurePassword,
                        textDirection: TextDirection.ltr,
                        textInputAction: TextInputAction.done,
                        autocorrect: false,
                        enableSuggestions: false,
                        autofillHints: const [AutofillHints.password],
                        onFieldSubmitted: (_) => _submit(),
                        decoration: InputDecoration(
                          labelText: AppStrings.password,
                          prefixIcon: const Icon(Icons.lock_outline_rounded),
                          suffixIcon: IconButton(
                            tooltip: _obscurePassword ? AppStrings.showPassword : AppStrings.hidePassword,
                            onPressed: () => setState(() => _obscurePassword = !_obscurePassword),
                            icon: Icon(_obscurePassword
                                ? Icons.visibility_outlined
                                : Icons.visibility_off_outlined),
                          ),
                        ),
                        validator: (value) =>
                            (value == null || value.isEmpty) ? AppStrings.passwordRequired : null,
                      ),
                      const SizedBox(height: AppSpacing.xl),
                      FilledButton(
                        onPressed: _submitting || !config.isConfigured ? null : _submit,
                        style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                        child: _submitting
                            ? SizedBox(
                                width: 20,
                                height: 20,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                  color: colorScheme.onSecondary,
                                ),
                              )
                            : const Text(AppStrings.signIn),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _Brand extends StatelessWidget {
  const _Brand();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      children: [
        Container(
          width: 56,
          height: 56,
          decoration: BoxDecoration(
            color: theme.colorScheme.primary,
            borderRadius: BorderRadius.circular(14),
          ),
          child: Icon(Icons.local_gas_station_rounded, color: theme.colorScheme.onPrimary, size: 30),
        ),
        const SizedBox(height: AppSpacing.lg),
        Text(AppStrings.appName, style: theme.textTheme.headlineSmall),
        const SizedBox(height: AppSpacing.xs),
        Text(AppStrings.appSubtitle, style: theme.textTheme.bodySmall),
      ],
    );
  }
}
