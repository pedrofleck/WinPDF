<!--
  WinPDF Engineering & Performance Guidelines
  Rule file automatically loaded by agentic coding assistants.
-->

# WinPDF Engineering & Performance Guidelines

These guidelines are mandatory for all AI agents and engineers working on WinPDF.
They supersede generic coding suggestions and must be respected in every commit.

---

## 1. Strict Priority Hierarchy

1. **PRIORITY #1 — Zero Consumption in Idle (0.0% CPU / 0% GPU when stationary):**
   Battery life on mobile Windows 11 devices (Surface, laptops, tablets) is paramount. When no user gesture or document loading is in progress, CPU and GPU activity must drop to absolute zero.
2. **PRIORITY #2 — Minimal CPU Usage during Scroll & Zoom:**
   Always trade a controlled amount of RAM (page/bitmap caches) to eliminate redundant CPU re-parsing and rasterization cycles.
3. **PRIORITY #3 — Controlled RAM & Zero GC Pressure (Zero LOH):**
   Bounded LRU caches with strict byte/handle limits. Zero allocations in the Large Object Heap (LOH > 85 KB) to eliminate Gen 2 GC pauses.
4. **NON-NEGOTIABLE UI PRESERVATION (UI Freeze):**
   Never simplify, degrade, or redesign the current native WinUI 3 interface (Mica Base backdrop, unified 48 DIPs TitleBar, directional `Frame` slide transitions, resizable sidebar with custom splitter, and responsive 2-column `SettingsView`).

---

## 2. Idle State: True 0.0% CPU and 0% GPU

- **Forbidden Controls & Patterns:**
  - Never use `Microsoft.Graphics.Canvas.UI.Xaml.CanvasAnimatedControl` (it runs a continuous render loop).
  - Never use `DispatcherTimer`, polling loops (`while (true) await Task.Delay()`), or infinite XAML storyboard animations.
  - Never issue continuous background telemetry or heartbeats.
- **Stationary Display:**
  - When a document is open and stationary on screen, `Invalidate()` must **never** be invoked.
  - This allows the CPU to enter Deep C-States (C8/C10) and the display panel to activate Panel Self-Refresh (PSR).
- **PDFium Thread Sleeping:**
  - The single dedicated PDFium render thread must block asynchronously using `ChannelReader.WaitToReadAsync()`.
  - Never spin-wait (`Thread.SpinWait`) or poll with `Thread.Sleep`.

---

## 3. Hybrid Full-Page vs. Tiled Rendering Strategy

In PDFium, each call to `FPDF_RenderPageBitmap` traverses the entire page display list (vector paths, fonts, raster images) on the CPU. Indiscriminate 512×512 tiling multiplies CPU overhead significantly for regular reading.

- **ZoomFactor <= 1.5 (Standard Reading Mode):**
  - **Do NOT tile the page.** Render the complete page in **a single rasterization call** at quantized zoom `Zq`.
  - **At Zq == 1.0:** The Level 0 base bitmap is already final. Do **not** re-render after the 150 ms gesture debounce.
- **ZoomFactor > 1.5 (High Zoom / Magnification):**
  - Switch to 512×512 tiles using `CanvasVirtualControl.RegionsInvalidated`.
  - Render tiles strictly for regions intersecting the current viewport.

---

## 4. RAM Trade-Offs to Preserve CPU & Eliminate GC Pauses

### 4.1 Native Page Handle Cache (`PdfiumPageCache`)
- PDFium page loading (`FPDF_LoadPage`) parses page dictionaries, decompresses zlib streams, and resolves embedded font programs on the CPU.
- Maintain an LRU cache on the PDFium thread retaining the **last 10 opened `FPDF_PAGE` handles**.
- Re-accessing recently viewed pages during zoom changes, scrolling, or page flips must reuse existing handles without re-parsing.

### 4.2 Zero-LOH / Zero-Copy Memory Pipeline
- **Forbidden:** Never allocate `new byte[]` for rendered pages or tiles. Any buffer >= 85,000 bytes goes directly to the Large Object Heap (LOH), triggering expensive Gen 2 Garbage Collections that stutter UI threads.
- **Native Allocation:** Allocate native bitmaps via `FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRA, IntPtr.Zero, 0)`.
- **Direct GPU Upload:** Retrieve the native pointer via `FPDFBitmap_GetBuffer` and feed `CanvasBitmap.CreateFromBytes` / native interop directly without intermediate managed byte array copies (or use a single pre-allocated reusable native buffer per thread).

---

## 5. Strictly Lazy Sidebar Rendering

- Thumbnail cards in `ThumbnailsTabPage` must **only** be rasterized when:
  1. `ShellViewModel.IsSidebarOpen == true`, **AND**
  2. `ShellViewModel.SidebarTab == SidebarTab.Thumbnails`.
- Requests must only be dispatched for items currently realized and visible within the virtualized `ListView` (`ItemsStackPanel`).
- When the sidebar is closed or switched to Outline/Annotations, all background thumbnail jobs must be cancelled immediately.

---

## 6. Shell & UI Freeze

- The existing Shell architecture is locked:
  - `MainWindow.xaml`: Mica Base backdrop, unified 48 DIPs TitleBar, integrated buttons, 3-column resizable layout with `SplitterContainer`.
  - `SettingsView.xaml`: 2-column responsive layout with unboxed MS Paint-style About panel.
  - `SelectorBar` in sidebar footer and modular tab pages under `Views/Sidebar/`.
- In Steps 3 and 4, changes to Shell views are strictly limited to hosting the render canvas controls inside `DocumentView.xaml` and `ThumbnailsTabPage.xaml`. Do not modify existing styles, paddings, or control hierarchies.
