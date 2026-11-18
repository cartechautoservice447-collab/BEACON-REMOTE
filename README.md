# Remote Desk

PHASE 1 — BUILD THE APPLICATION FOUNDATION AND MOBILE DASHBOARD

Now begin implementation of Phase 1 of my complete remote laptop-control product.

IMPORTANT: This is a phased development project. Implement ONLY Phase 1. Do not attempt to implement every feature at once.

1. First inspect the project

Before editing:

- Inspect the existing source code and folder structure.
- Check the installed dependencies.
- Check whether Lovable Cloud / Supabase is already connected.
- Preserve existing working functionality.
- Reuse existing components and services where appropriate.
- Do not create duplicate backend projects or unnecessary dependencies.

2. Design the application

Build a professional, premium-quality mobile-first dashboard.

Design priorities:

- Optimized primarily for Android smartphones.
- Smooth and lightweight interface.
- Modern, clean design.
- Dark theme as the default, with a consistent light theme option.
- Consistent spacing, typography and icons.
- Accessible contrast and readable controls.
- Subtle transitions only.
- No heavy animations or unnecessary visual effects.

The dashboard must work properly in portrait and desktop browsers.

Do not make the entire application landscape-only. Landscape orientation is specifically required for the actual remote desktop workspace, which will be implemented in a later phase.

3. Authentication

Implement real user authentication using the connected Lovable Cloud / Supabase Auth infrastructure.

Include:

- Sign-up.
- Login.
- Logout.
- Password reset.
- Session persistence.
- Protected application routes.
- Appropriate authentication error handling.

Never store passwords in application tables or browser storage.

4. Main dashboard

Create a mobile-friendly home dashboard containing:

Header

- Application identity.
- User account menu.
- Settings access.
- Connection status summary.

Registered laptops

- Laptop name.
- Operating system.
- Online/offline status.
- Last connected time.
- Connection action.
- Device settings.
- Remove device option.

Main actions

- Add Laptop.
- Pair New Device.
- View Registered Devices.
- Connection History (can display an honest empty state until implemented).

When the user has no registered laptops, display a beautiful empty state explaining how to connect their first laptop.

Do not populate the interface with invented laptops or fake online statuses.

5. Device registration and database

Create the necessary database structure.

Suggested tables:

- profiles
- devices
- pairing_sessions

Each device must belong to an authenticated user.

Include:

- Unique device identifier.
- Device name.
- Operating system.
- Agent version.
- Connection status.
- Last heartbeat timestamp.
- Registration timestamp.
- Updated timestamp.

Implement appropriate database constraints and Row Level Security policies.

A user must never be able to read, modify or delete another user's devices.

Device online status must eventually be driven by authenticated companion-agent heartbeats. Never mark a device online merely because the dashboard was opened.

6. Pairing interface

Build the laptop-pairing experience.

Provide:

- QR-code pairing screen.
- Six-digit pairing code entry.
- Pairing instructions.
- Waiting-for-laptop confirmation screen.
- Pairing expiration indicator.
- Cancel pairing.
- Successful pairing state.
- Error and retry states.

Security requirements:

- Generate pairing secrets on the server.
- Use short-lived, single-use pairing credentials.
- Store appropriate hashes, not exposed pairing secrets.
- Apply attempt limits and expiration.
- Never expose service-role keys in frontend code.
- Never mark a laptop successfully paired without a valid backend confirmation.

Prepare the backend interface for the future Windows companion agent. If the agent is not yet available, show a genuine waiting state rather than simulating successful pairing.

7. Application settings

Create an initial settings screen with:

- Account information.
- Theme preference.
- Registered device management.
- Security settings placeholder.
- Application information.
- Logout.

Do not add decorative or nonfunctional controls that pretend to work.

8. Backend security

Implement:

- Authentication enforcement.
- Row Level Security.
- Owner-only device access.
- Secure server-side pairing operations.
- Input validation.
- Appropriate error responses.
- Protected backend secrets.

Keep all security-critical pairing operations outside the client.

9. Future compatibility

Structure the source code so future phases can integrate:

- WebRTC streaming.
- Supabase Realtime signalling.
- Windows companion authentication.
- Mouse Mode.
- Touch Mode.
- Virtual keyboard.
- Audio streaming.
- File transfer.
- Clipboard synchronization.
- Remote system controls.

Use reusable components and clean separation of responsibilities.

Do not implement those integrations in this phase.

10. Explicitly out of scope

Do NOT implement in Phase 1:

- Actual laptop screen streaming.
- WebRTC remote desktop sessions.
- Mouse or keyboard injection.
- Native Windows companion service.
- Fake laptop connections.
- Fake screen previews.
- Fake connection-quality metrics.
- Simulated file transfers.

These will be developed and tested in later phases.

11. Testing and verification

After implementation:

1. Test account registration and login.
2. Test logout and session persistence.
3. Test protected routes.
4. Test device database operations.
5. Test Row Level Security.
6. Test pairing expiration and cancellation.
7. Test empty states.
8. Test mobile responsiveness at 360px, 390px and 430px widths.
9. Test tablet and desktop layouts.
10. Check browser console and network errors.
11. Verify that no secrets are exposed in frontend bundles.

Fix errors discovered during testing.

12. Completion report

When finished, provide:

- Features actually implemented.
- Files created.
- Files modified.
- Database tables and migrations.
- Security policies implemented.
- Tests executed and their results.
- Any unresolved problems.
- Features intentionally deferred to Phase 2.

Do not claim that a test passed unless it was actually executed.

Do not move to Phase 2 automatically.

Complete Phase 1, verify it and wait for my next instructions.

This project was built with [Lovable](https://lovable.dev).

## Build with Lovable

Continue developing this project in the [Lovable editor](https://lovable.dev/projects/1bd35e1a-c9d0-49aa-bcd8-4c5f581647b5).

- **Ship faster**: describe what you want to build and Lovable handles the code.
- **Stay in sync**: every change made in Lovable is committed straight to this repository.
- **Full ownership**: this code is yours. Push to `main` on GitHub and your changes sync back into Lovable, ready for your next prompt.

## Development

Prefer working locally? You need Node.js and npm — [install with nvm](https://github.com/nvm-sh/nvm#installing-and-updating).

```sh
git clone <this-repository-url>
cd <repository-name>
npm i
npm run dev
```
