# Rayforge Unity Library

A collection of personal Unity utilities, wrappers, extension methods, and helper systems designed to streamline common tasks and reduce boilerplate code.

## Design Philosophy

Some traditional OOP concepts, such as deep inheritance hierarchies, interfaces, and extensive use of virtual members, are **intentionally minimized** in this library. This is primarily for performance reasons in real-time 3D rendering. Examples of considerations include:

- **Virtual members and interfaces** require vtable lookups, preventing direct member resolution via `this ptr + offset`.
- **Heap allocations** introduce kernel calls and latency.
- **Pointer-based composition** across scattered objects can cause cache misses and memory indirection.

To mitigate these issues, parts of the library are designed to be **flat, modular, and type-specific**:

- Flat structures reduce runtime indirection and improve hot-path performance.
- Modular components are focused on single responsibilities for predictable access and reusability.
- Systems are optimized for large-scale per-frame operations without introducing additional CPU overhead.

These are just a few examples of the design choices made; there are certainly other aspects considered to ensure **efficiency, clarity, and maintainability**. The library aims to balance modularity with performance, consciously avoiding unnecessary OOP overhead where it matters most.

> **Note:** These optimizations are only meaningful in environments where **milliseconds of CPU/GPU time matter**, such as high-performance real-time 3D rendering. In standard application or UI code, the overhead from virtual calls or heap allocations is usually negligible.

## Installation

1. Open your Unity project.
2. Navigate to **Window → Package Manager**.
3. Click the **`+`** button in the top-left corner.
4. Select **“Install package from Git URL…”**.
5. Paste the repository URL and confirm.
