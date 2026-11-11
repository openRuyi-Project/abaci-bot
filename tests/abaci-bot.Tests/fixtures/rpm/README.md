# Test RPM Fixtures

This directory contains minimal binary RPM packages utilized exclusively for automated test verification of RPM header parsing, payload decompression across multiple compression formats, and ELF64 binary ABI comparison.

## Fixture Inventory

### 1. Multi-Compression Format Test Matrix (`acl-*.rpm`)
- **Files**:
  - `acl-zstd.rpm` (Payload compression: zstd, `%_binary_payload w19.zstdio`)
  - `acl-gzip.rpm` (Payload compression: gzip, `%_binary_payload w9.gzdio`)
  - `acl-xz.rpm` (Payload compression: xz, `%_binary_payload w7.xzdio`)
  - `acl-bzip2.rpm` (Payload compression: bzip2, `%_binary_payload w9.bzdio`)
  - `acl-lzma.rpm` (Payload compression: lzma, `%_binary_payload w7.lzdio`)
- **Upstream Source**: Access Control Lists utility (`acl 2.3.1`)
- **License**: LGPL-2.1-or-later for shared libraries (`libacl.so.1.1.2301`), GPL-2.0-or-later for utilities
- **Build Invocation**:
  ```bash
  rpmbuild -bb --define "_rpmformat 6" --define "_binary_payload <compression_tag>" acl.spec
  ```
- **Purpose**: Verifies that the pure managed ELF diff engine correctly parses RPM v6 headers (`lead=4`, `07070X` payload magic) across all five compression algorithms supported by modern RPM distributions.

### 2. Synthetic & Synthetic Delta Fixtures (`demo-lib-*.rpm`, `v6demo-*.rpm`, `sigchange-*.rpm`)
- **Files**:
  - `demo-lib-1.0-1.x86_64.rpm` / `demo-lib-2.0-1.x86_64.rpm`
  - `v6demo-1.0-1.x86_64.rpm` / `v6demo-2.0-1.x86_64.rpm`
  - `sigchange-1.0-1.x86_64.rpm` / `sigchange-2.0-1.x86_64.rpm`
- **License**: MIT
- **Purpose**: Minimal synthetic packages containing stub ELF64 shared objects to exercise ABI change detection, symbol removals, soname bumps, and ABI break evaluation.
