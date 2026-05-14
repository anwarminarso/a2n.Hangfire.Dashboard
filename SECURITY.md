# Security Policy

## Supported Versions

| Version | Supported          |
|---------|--------------------|
| 1.x     | ✅ Active support  |
| < 1.0   | ❌ Pre-release, no security patches |

## Reporting a Vulnerability

If you discover a security vulnerability in this project, please report it responsibly.

**Do NOT open a public GitHub issue for security vulnerabilities.**

Instead, please send an email to:

📧 **anwar.minarso@gmail.com**

Include the following information:

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

## Response Timeline

- **Acknowledgment**: Within 48 hours of receiving the report.
- **Assessment**: Within 7 days, we will assess the severity and confirm whether it is a valid vulnerability.
- **Fix**: Critical vulnerabilities will be patched within 14 days. Non-critical issues will be addressed in the next release cycle.
- **Disclosure**: We will coordinate with the reporter on public disclosure timing.

## Security Considerations

This dashboard is designed to be deployed within trusted networks. Please consider the following:

### Authentication & Authorization

- The dashboard does **not** include built-in authentication.
- Use the `DashboardOptions.Authorization` filters or your application's authentication middleware to restrict access.
- Never expose the dashboard endpoint to the public internet without authentication.

### Data Exposure

- The dashboard displays job parameters, exception details, and console output.
- Ensure sensitive data is not logged to job parameters or console output in production.

### SignalR

- The SignalR hub (`/hubs/dashboard`) is used for realtime updates.
- It should be protected by the same authentication as the dashboard itself.

### Dependencies

- Third-party JavaScript libraries (Bootstrap, Chart.js, Moment.js) are vendored locally — no CDN dependencies.
- We monitor dependencies for known vulnerabilities and update them in patch releases.

## Best Practices

1. Always deploy behind authentication (OAuth2, Azure AD, cookie auth, etc.)
2. Use HTTPS in production
3. Restrict dashboard access to operations/admin roles
4. Review job parameters for sensitive data before enabling the dashboard
5. Keep the package updated to receive security patches
