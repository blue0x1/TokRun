# TokRun v1.0

<img width="2172" height="724" alt="image" src="https://github.com/user-attachments/assets/2496e1e7-0dfb-4bf6-a839-7d1d4f3b4aa9" />


TokRun is a local Windows token-based process launcher for authorized administration, lab, and red-team environments.

It enumerates accessible process tokens on the host, selects a token by user name or PID, duplicates it as a primary token, and launches either a visible `cmd.exe` shell or a target executable under that token.

TokRun does not dump credentials, dump LSASS, create new logon sessions from passwords, install persistence, or disable security products. The target identity must already have an accessible token on the machine.

<img width="857" height="204" alt="image" src="https://github.com/user-attachments/assets/b4b943b8-cb16-4b00-9a5c-96c649c519ce" />

## Status

This is the initial v1.0 build.

Current focus:

- Clean default output for normal use.
- Verbose tracing for token selection and launch fallbacks.
- Reliable visible shell behavior on Windows 10, Windows 11, and modern Server builds.
- Strict `--pid` + `--user` validation to avoid launching with the wrong identity.

## Supported Platforms

TokRun targets .NET Framework 4.0 for wide compatibility.

Designed for:

- Windows 7+
- Windows 10
- Windows 11
- Windows Server 2008 R2+
- Windows Server 2012 / 2016 / 2019 / 2022
- Windows Server 2025 / build 26100+

## Build

From a Developer Command Prompt or a machine with MSBuild available:

```cmd
build.bat
```

The build script tries MSBuild first and builds Release outputs for `Any CPU`, `x64`, and `x86`. If MSBuild is not available, it falls back to the .NET Framework 4 `csc.exe` compiler and builds:

```text
TokRun\bin\Release\x64\TokRun.exe
TokRun\bin\Release\x86\TokRun.exe
```

## Quick Start

List accessible process tokens:

```cmd
TokRun.exe --list
```

Open a visible shell as a matching user token:

```cmd
TokRun.exe --user DOMAIN\user --cmd
```

Run a local executable as a matching user token:

```cmd
TokRun.exe --user DOMAIN\user --run C:\Tools\agent.exe
```

Pass arguments to the target executable:

```cmd
TokRun.exe --user DOMAIN\user --run C:\Tools\agent.exe --args "--mode check"
```

Set a working directory:

```cmd
TokRun.exe --user DOMAIN\user --run C:\Tools\agent.exe --workdir C:\Tools
```

Launch hidden:

```cmd
TokRun.exe --user DOMAIN\user --hidden --run C:\Tools\agent.exe
```

Show detailed token selection and Win32 fallback errors:

```cmd
TokRun.exe --user DOMAIN\user --cmd --verbose
```

## SYSTEM Tokens

TokRun accepts common SYSTEM identity forms. These are equivalent for token matching:

```cmd
TokRun.exe --user SYSTEM --cmd
TokRun.exe --user "NT AUTHORITY\SYSTEM" --cmd
```

Hidden SYSTEM launch example:

```cmd
TokRun.exe --user SYSTEM --hidden --run C:\Tools\agent.exe
```

Validate that a specific PID belongs to SYSTEM before launching:

```cmd
TokRun.exe --pid 1234 --user SYSTEM --cmd
TokRun.exe --pid 1234 --user "NT AUTHORITY\SYSTEM" --cmd
```

If the PID token does not belong to SYSTEM, TokRun refuses to launch instead of silently continuing with another token.

## PID Mode

Use a token from a specific process:

```cmd
TokRun.exe --pid 1234 --cmd
TokRun.exe --pid 1234 --run C:\Tools\agent.exe
```

Combine `--pid` with `--user` when you want validation:

```cmd
TokRun.exe --pid 1234 --user DOMAIN\user --cmd
```

In validation mode, the PID token must match the requested identity. TokRun will not fall back to a different user.

## Session Selection

Filter source tokens by session:

```cmd
TokRun.exe --user DOMAIN\user --session 2 --cmd
```

Request a target interactive session:

```cmd
TokRun.exe --user DOMAIN\user --target-session 2 --cmd
```

TokRun ranks matching tokens and prefers tokens already in the selected interactive session.

## Options

```text
--list                 List accessible process tokens
--user <name>          Match DOMAIN\user, .\user, bare user name, SYSTEM, or NT AUTHORITY\SYSTEM
--pid <pid>            Use a token from a specific process
--session <id>         Filter source tokens by session ID
--cmd                  Launch a visible cmd.exe
--run <path>           Launch a local target executable
--args <args>          Arguments passed to the target executable
--workdir <path>       Working directory for the child process
--target-session <id>  Preferred interactive session for visible launches
--hidden               Launch without a visible window
--verbose              Show token candidates, fallback attempts, and Win32 errors
--help                 Show built-in help
```

`--cmd` and `--run` are mutually exclusive.

## Output

Default output is intentionally short:

```text
[+] Selected token : DOMAIN\user
[+] Launch target  : cmd.exe
[+] Started PID    : 2140
[+] Session        : 2
```

Use `--verbose` when diagnosing token access, session behavior, Windows Terminal detection, or failed process-creation attempts.

## Launch Behavior

TokRun automatically tries compatible launch methods for the selected token:

1. Duplicates the selected token as a primary token.
2. Prefers matching tokens in the requested interactive session.
3. On Windows 11 and modern Server builds, tries Windows Terminal (`wt.exe`) for visible console launches.
4. Falls back to a native console broker when needed.
5. Tries `CreateProcessWithTokenW` and `CreateProcessAsUserW` launch paths.
6. If permitted, moves the duplicated token to the requested session and retries.
7. Tries linked tokens and remaining matching candidates.

For Windows Terminal discovery, TokRun checks:

- `%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe`
- `SearchPath` / `PATH`
- `HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\wt.exe`
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wt.exe`
- `C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_*__xxxxx\wt.exe`

## Required Privileges

Run TokRun from an elevated shell.

Depending on the source token and launch method, Windows may require privileges such as:

- `SeDebugPrivilege`
- `SeImpersonatePrivilege`
- `SeAssignPrimaryTokenPrivilege`
- `SeIncreaseQuotaPrivilege`

TokRun attempts to enable common privileges automatically when they are assigned to the caller. If a privilege is not assigned, TokRun cannot enable it.

## Limitations

- The target user must already have a token on the host.
- TokRun cannot create a token without credentials.
- UNC/share paths are rejected for `--run`; use local executable paths only.
- Some protected processes deny token access even from an elevated shell.
- Visible shells depend on Windows session, desktop, terminal, and privilege behavior.
- Remote execution is out of scope; TokRun launches local processes only.

## Authorized Use Only

Use TokRun only in systems you own or are explicitly authorized to assess or administer.
