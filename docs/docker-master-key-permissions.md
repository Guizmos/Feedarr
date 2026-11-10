# Persistent master key permissions (P2-01)

The old recursive chmod added group-read permission to an existing .master-key on
container restart (0600 became 0640). The entrypoint now excludes keys from that
operation, then applies owner-only directories (0700) and files (0600) inside keys.
Failure to set secret ownership or permissions stops startup explicitly.

FileMasterKeyProvider also repairs and verifies 0700 on the keys directory and 0600
on an existing master key before reading it. New keys use CreateNew and UnixCreateMode
0600 so creation never overwrites an existing key or starts with broad permissions.
Incorrect permissions never trigger key regeneration. Permission errors and POSIX
filesystems that fail to preserve the modes cause an explicit startup exception.
The AES-GCM format, existing key bytes and environment-key derivation are unchanged.
Windows bypasses POSIX mode operations; Windows ACL hardening is outside this change.

Volumes must support owner access for the configured PUID/PGID and enforce POSIX modes.
Read-only volumes, incompatible NAS ownership/ACLs or non-POSIX filesystems may now
refuse startup instead of silently running with insecure secrets. Operators should
verify effective ACLs in addition to mode bits and keep the existing master key backed up.

Tests execute the real entrypoint with real find/chmod on a temporary directory,
while stubbing identity/ownership changes and application launch. They cover a second
start on the same directory, unchanged key bytes and explicit chmod/chown failure.
.NET tests verify key creation, permission repair, restart and environment precedence.
These are not a real Docker deployment test. Docker/container restart, actual PUID/PGID
remapping and NAS persistent-volume behavior still require validation on the target host.
