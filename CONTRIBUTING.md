# Contributing

Thank you for contributing to Remote Assist.

1. Open an issue for substantial behavioral or protocol changes before implementation.
2. Keep attended-consent and revocation guarantees intact.
3. Add or update tests for security-sensitive behavior.
4. Run `go test ./...`, `go vet ./...`, `bash -n deploy/*.sh`, and `node --check internal/webui/web/app.js` before submitting a pull request.
5. Never commit credentials, private keys, live certificates, production database dumps, customer data, or access tokens.
6. Keep upstream product identity generic; forks can apply their own branding.

By contributing, you agree that your contribution is licensed under the MIT License.
