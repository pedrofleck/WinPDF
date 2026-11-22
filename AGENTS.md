# AGENTS.md — WinPDF engineering guide for AI agents and humans

> **Read this file completely before changing any code.** It is the single source of truth for
> architecture, rendering pipeline, coordinate math, conventions and roadmap. If you change any of
> these, update this file in the same commit.
>
> **Mandatory Progress Tracking for AI Agents:** You **MUST** continuously update `AGENTS.md` and
> `TODO.md` as work progresses — marking completed items, documenting architectural decisions and
> file links for what was built, and keeping the remaining task list accurate for future sessions
> and agents.

WinPDF is a modern, lightweight, 100% native PDF reader and annotator for Windows 11, inspired by
the design, UX and feature set of [GNOME Papers](https://gitlab.gnome.org/GNOME/papers), built on
WinUI 3 (Windows App SDK) + C# / .NET 10 (LTS).

---

## 0. Current status & Roadmap

| Step | Target Version | Scope | Status |
|------|----------------|-------|--------|
| 1 | `v0.1.x` | Solution, `.csproj` (unpackaged), `AGENTS.md`, `en-US` / `pt-BR` resources, .NET 10 migration | ✅ Done |
| 2 | `v0.2.x` | Shell: Mica, custom TitleBar, `SplitView` sidebar, `CommandBar`, view-mode selector, MVVM wiring | ✅ Done |
| 3A | `v0.3.x` | `IPdfService` (PDFium, dedicated thread, Zero-LOH/Zero-Copy), coordinate math, LRU caches, lazy sidebar thumbnails | ✅ Done |
| 3B | `v0.4.x` | `PdfPageCanvas` (Win2D), DirectManipulation zoom/pan, hybrid full-page / detail patch, continuous & paged views | ✅ Done |
| 3C | `v0.5.x` | Interactive text selection (`FPDFText_*`), touch gesture suite (slide swipe, long-press selection handles & floating bar, touch scroll scrubber), full-document search (`Ctrl+F`), clickable links (`FPDFLink_*`), GPU highlights | ✅ Done (`v0.5.0` – `v0.5.4`) |
| 4A | `v0.6.0-alpha.6` | Text markups (Highlight, Underline, Squiggly, StrikeOut), speech-bubble Comment Notes (`FPDF_ANNOT_TEXT`), external shapes/ink manipulation (drag-to-move, Copy/Cut/Paste at last click preserving `/AP` Form XObject colors), 6 preset colors, 40-step Undo (`Ctrl+Z`), zero-flicker Annotations sidebar, and PKCS#7 Digital Signatures (`FPDFSigObj_*` + `FPDF_FFLDraw` visual stamps + status bar badge) | ✅ Done (`v0.6.0-alpha.6`) |
| 4B | `v0.6.x` (`0.6.1+`) | Interactive freehand Pen/Ink drawing (`AnnotationTool.Pen`) with live Win2D wet-ink preview, Eraser tool (`AnnotationTool.Eraser`), Redo (`Ctrl+Y`), in-place Save (`Ctrl+S`) + unsaved-changes guard, and annotation settings (keep tool active, ink smoothing, stroke timeout, auto-save, stylus mode) | 📋 Next Up |
| 5 | `v0.7.x` | **Multi-Document Tabbed Shell** (`TabView`, `OpenDocuments` state isolation, background tab RAM/disk hibernation, `Ctrl+T`/`Ctrl+W`/`Ctrl+Tab`, single-instance `.pdf` routing), Share menu, Taskbar Jump List, AcroForms filling, attachment export, persistent bookmarks, auto-updater, and `.msix` Store packaging | 🗓️ Planned (`v0.7.x`) |

Sections marked **(design)** describe the agreed target design not yet implemented. Turn them into
**(implemented)** with file links when the code lands.

---

## 1. Non-negotiable constraints

1. **Stack:** WinUI 3 (Windows App SDK 1.8.x) + C# + .NET 10 (`net10.0-windows10.0.26100.0`,
   minimum OS `10.0.19041.0`). No WPF, no WinForms, no WebView-based UI.
2. **No paid/proprietary PDF components** (no Syncfusion, DevExpress, Apryse, etc.).
   PDF engine = **PDFium** (`bblanchon.PDFium.Win32` binaries + hand-written P/Invoke).
   GPU drawing = **Win2D** (`Microsoft.Graphics.Win2D`).
3. **MVVM** with `CommunityToolkit.Mvvm` (source generators: `[ObservableProperty]`, `[RelayCommand]`).
4. **Strict Performance & Engineering Hierarchy:**
   - **PRIORITY #1 — Zero Idle Consumption (0.0% CPU / 0% GPU when stationary):** Mandatory for mobile battery life and PSR.
   - **PRIORITY #2 — Minimum CPU Usage during Scroll/Zoom:** Always trade controlled RAM (page/bitmap caches) to avoid CPU display list re-traversals.
   - **PRIORITY #3 — Controlled RAM & Zero-LOH:** Predictable LRU limits; never allocate buffers in the Large Object Heap (> 85 KB).
   - **NON-NEGOTIABLE UI PRESERVATION (UI Freeze):** Never degrade or simplify current native WinUI 3 shell layout (Mica Base, 48 DIPs TitleBar, frame navigation, resizable sidebar, 2-column SettingsView).
5. **Zero hardcoded user-visible strings** in XAML or C#. See §6.
6. **All code, identifiers, comments and commit messages in English.** XML docs (`/// <summary>`)
   on every public/protected type and member (the build emits CS1591 warnings for gaps).
7. **Continuous Documentation & Progress Tracking:** Whenever you complete a feature, fix a bug,
   or refine the roadmap, update `AGENTS.md` and `TODO.md` before finishing your task so the next
   agent or conversation has an exact record of what is implemented and what remains.

---

## 2. Repository layout

```
WinPDF/
├─ AGENTS.md                    ← this file
├─ README.md
├─ WinPDF.sln                   ← platforms: x64, x86, ARM64 (no AnyCPU)
├─ global.json                  ← .NET SDK 10.0.x (rollForward: latestFeature)
├─ Directory.Build.props        ← shared compiler settings (nullable, docs, analyzers)
├─ Directory.Packages.props     ← Central Package Management (ALL NuGet versions live here)
├─ .editorconfig
├─ tools/
│  └─ Check-Resources.ps1       ← verifies en-US / pt-BR key parity
└─ src/WinPDF/
   ├─ WinPDF.csproj             ← WinUI 3, unpackaged, self-contained WinAppSDK
   ├─ app.manifest              ← PerMonitorV2 DPI awareness
   ├─ App.xaml(.cs)             ← entry point, (Step 2) DI container + activation args
   ├─ MainWindow.xaml(.cs)      ← window shell
   ├─ Properties/launchSettings.json
   ├─ Strings/
   │  ├─ en-US/Resources.resw   ← DEFAULT / fallback language
   │  └─ pt-BR/Resources.resw
   ├─ Helpers/                  ← ResourceStrings, ResourceKeys, small utilities
   ├─ Models/            (S2+)  ← plain records: PdfPageInfo, OutlineItem, PdfTextModels, Annotation models
   ├─ ViewModels/        (S2+)  ← ShellViewModel, DocumentViewModel, PageViewModel, ThumbnailViewModel
   ├─ Views/             (S2+)  ← XAML pages / user controls (no business logic in code-behind)
   ├─ Controls/          (S3+)  ← PdfPageCanvas, SplitterContainer, ThumbnailPresenter
   ├─ Services/          (S3+)  ← IPdfService, PdfiumService, PdfiumEngine, IFilePickerService, IDialogService
   ├─ Rendering/         (S3+)  ← TileCache, PdfiumPageCache, ZoomQuantizer, PageCoordinateTransform
   └─ Interop/Pdfium/    (S3+)  ← [LibraryImport] declarations, SafeHandles, PDFium enums/structs
```

---

## 3. MVVM architecture **(implemented)**

```mermaid
flowchart LR
  subgraph View["View (XAML + thin code-behind)"]
    MW[MainWindow / ShellView]
    DV[DocumentView]
    PC[PdfPageCanvas - Win2D]
  end
  subgraph VM["ViewModels (CommunityToolkit.Mvvm)"]
    SVM[ShellViewModel]
    DVM[DocumentViewModel]
    TVM[ThumbnailItemViewModel]
    OVM[OutlineItemViewModel]
  end
  subgraph SVC["Services (UI-agnostic, async, thread-safe)"]
    PDF[IPdfService → PdfiumService]
    FP[IFilePickerService]
    DLG[IDialogService]
    TC[TileCache + PdfiumPageCache]
  end
  MW --> SVM
  DV --> DVM
  PC --> DVM
  SVM --> DVM
  DVM --> TVM
  DVM --> OVM
  DVM --> PDF
  PC --> PDF
  PC --> TC
  SVM --> FP
  SVM --> DLG
```

Rules:

- **ViewModels never reference `Microsoft.UI.Xaml.*` types** (except `DispatcherQueue` via an
  injected abstraction). They are unit-testable without a window.
- **Services never touch UI.** They return immutable data (records, byte buffers, `CanvasBitmap`
  is allowed because Win2D resources are device-bound, not thread-bound).
- **DI:** `Microsoft.Extensions.DependencyInjection` configured in `App.xaml.cs`.
  Services are singletons; `DocumentViewModel` is created per open document.
- **Commands:** `[RelayCommand]` (async commands use `AsyncRelayCommand` with cancellation).
- **Observable Properties:** In .NET 10 (C# 13/14) with CommunityToolkit.Mvvm 8.4+, use partial
  property syntax (`[ObservableProperty] public partial T Property { get; set; }`) with XML docs
  `/// <summary>` directly on the property so generated code satisfies `CS1591` without warnings.
- **Code-behind** is allowed only for pure view concerns that cannot be expressed in XAML
  (pointer handling, Win2D draw callbacks, `ScrollViewer.ViewChanged` → forwarded to VM/scheduler).

---

## 4. Rendering pipeline, GPU and touch **(implemented)**

### 4.1 Threads & Idle State

| Thread | Does | Never does |
|--------|------|-----------|
| **UI thread** | Layout, XAML, Win2D draw callbacks (blits cached bitmaps + vector highlights), pointer events | Call PDFium, decode, allocate large bitmaps |
| **Compositor / DManip thread** (system) | Applies pan/zoom transforms to the visual tree on the GPU at display refresh rate | — (not under our control, that is the point) |
| **PDFium render thread** (1, dedicated) | *All* `FPDF_*` calls, rasterization into BGRA buffers, text/search/link queries, save | Touch UI objects, spin-wait, sleep |
| **Thread pool** | `CanvasBitmap.CreateFromBytes` upload to GPU, cache eviction bookkeeping | Call PDFium |

PDFium is **not thread-safe**. `PdfiumService` owns a single long-running thread that consumes
priority channels. Every public `IPdfService` method enqueues a job and returns a `Task`.

**Idle 0.0% CPU & 0% GPU (Priority #1):**
- Prohibited: `CanvasAnimatedControl`, continuous `DispatcherTimer`, polling loops, infinite XAML animations.
- When stationary on screen, `Invalidate()` is **never** called. This allows the CPU to enter Deep
  C-States (C8/C10) and the display panel to activate Panel Self-Refresh (PSR).

### 4.2 Hybrid Level 0 Base, Single Viewport Patch, and GPU Text/Search Highlights

1. **Level 0 Base Bitmap (`ZoomFactor <= 1.01` or background preview):**
   - Render the complete page in **a single rasterization call** at base resolution (1.0x).
   - Displayed via `_baseCanvas` (`CanvasControl`) with `DpiScale = 1.0f` fixed forever.
   - Cached in `TileCache` (`TileKind.FullPage, ZoomStep: 0`). Guarantees no page is ever blank during fast scroll.
2. **High Zoom / Magnification (`ZoomFactor > 1.01`):**
   - Displayed via `_detailCanvas` (`CanvasControl`), positioned over the visible viewport via `_detailTranslate`.
   - On gesture completion (85 ms debounce):
     - Compute visible page intersection `GetVisibleRectInPageDips()`.
     - Expand with 20% prefetch margin.
     - Render in **a single high-priority PDFium call** (`RenderAsync`), capped at 4096 px.
     - Upload `CanvasBitmap` and position `_detailCanvas`.
   - Off-screen pages immediately collapse `_detailCanvas` and release high-res VRAM via `ReleaseHighResPatch()`.
3. **GPU Vector Highlights (`DrawTextOverlays` in `PdfPageCanvas`):**
   - Search match highlights (translucent yellow for page matches, translucent orange + border for active match)
     and interactive text selection (translucent blue with rounded corners) are rendered as Win2D vector primitives
     directly inside `OnBaseCanvasDraw` and `OnDetailCanvasDraw` (after Night Mode bitmap inversion, so highlight
     colors remain legible in both Light and Night Mode with zero extra `CanvasControl` surfaces).
   - Only pages whose selection or active search match changes call `InvalidateOverlays()`, preserving 0.0% idle CPU/GPU.

### 4.3 Caches & Lazy Sidebar Loading

- `PdfiumPageCache`: LRU cache on the PDFium render thread holding the last **8 opened `FPDF_PAGE`
  handles** (`FPDF_LoadPage`) along with their companion lazy `FPDF_TEXTPAGE` (`FPDFText_LoadPage`)
  and `FPDF_PAGELINK` (`FPDFLink_LoadWebLinks`) handles. Companion handles are always disposed
  strictly before `FPDF_ClosePage`. Full-document background search (`SearchDocument`) reuses cached
  handles for visible pages and transiently opens/closes uncached pages so the visible LRU is never flushed.
- `TileCache`: LRU keyed by `(documentId, pageIndex, kind, zoomStep, tileX, tileY, renderFlags, rotation, scalePercent)`,
  bounded by **bytes**.

### 4.4 Touch & Precision Touchpad Architecture **(implemented in `v0.5.x`)**

1. **Interactive Horizontal Slide Swipe (`DocumentView.xaml(.cs)`):**
   - Active in Paged mode (`!doc.IsContinuous`) and Presentation mode (`F5`) for both 1-finger touch drag and 2-finger Precision Touchpad horizontal swipe (`IsHorizontalMouseWheel && !isShiftDown`).
   - Synchronously translates the current page while revealing `AdjacentSlidePreviewContainer` (`AdjacentLeftPageCanvas` / `AdjacentRightPageCanvas`) pre-populated from `TileCache` Level 0 / `s_slideBuffer`.
   - Includes edge rubber-banding on first/last page and velocity/distance commit vs. cubic ease-out bounce-back. Keyboard arrows, mouse wheel, and `PrevPageZone`/`NextPageZone` remain instant (no animation).
2. **Touch Long-Press Text Selection & Floating Action Bar (`PdfPageCanvas.cs`, `DocumentView.xaml(.cs)`):**
   - Holding a finger for `320 ms` within `10 DIPs` over selectable text engages `_isTouchSelecting`, cancels `DirectManipulation`, selects the initial word, and tracks drag selection with draggable start/end handles (`TouchSelectionStartHandle` / `TouchSelectionEndHandle`).
   - Releasing the finger opens the native WinUI 3 `CommandBarFlyout` (`TouchSelectionCommandBarFlyout`) above the selection with Copy, Highlight, Add Comment, Underline, Strikethrough, Squiggly, and 6 preset color swatches.
   - Holding `320 ms` on an empty page area triggers `TouchEmptyHoldRequested` to place a speech-bubble Comment Note (`FPDF_ANNOT_TEXT`).
3. **Touch Scroll Scrubber Pill (`DocumentView.xaml(.cs)`):**
   - Appears on the right edge of the document viewport exclusively during touch interaction in Continuous mode (`TouchScrollScrubberContainer`, `48×64 DIP` hit target + `26×48 DIP` Acrylic pill + live `TouchScrollPageBubble` badge during 1:1 fast scrubbing), auto-hiding after `2.2 s` of inactivity.

### 4.5 Annotations, Digital Signatures & Undo Architecture **(implemented in `v0.6.0-alpha.6`, remaining items in `v0.6.x`)**

1. **PDFium Annotation & FormFill Pipeline (`PdfiumEngine.cs`, `PdfiumNative.cs`):**
   - All PDFium annotation reads/mutations run strictly on the dedicated PDFium thread.
   - **FormFill Visual Stamps (`FPDFDOC_InitFormFillEnvironment` + `FPDF_FFLDraw`):** Initialized per opened document (`PdfiumFormHandle` disposed strictly before `FPDF_CloseDocument`) and invoked after `FPDF_RenderPageBitmap` so digital signature appearances (`/FT /Sig` widget stamps) and form widgets render identically on Level 0 base bitmaps, high-res detail patches, thumbnails, and print.
   - **Text Markups:**
     - `Highlight`: custom `/AP` stream with `/BM /Multiply` ExtGState (`ca 0.42`) and asymmetric vertical padding (`-6%` bottom, `+10%` top) so highlights wrap text comfortably without colliding across lines.
     - `Underline`: `/AP` stroke positioned at `-6%` below the quad bottom baseline.
     - `Squiggly`: `/AP` continuous sine-wave stroke (`1.15 pt` width) at `-5%` below baseline; UI uses a custom 15×15 Fluent `W` + wavy underline `PathIcon`.
     - `StrikeOut`: `/AP` stroke at `42%` of quad height.
   - **Comment Notes (`FPDF_ANNOT_TEXT`):** Fixed `22×22 pt` speech-bubble vector `/AP` stream with darker border stroke and 2 interior lines.
   - **External Shapes & Ink (`Ink`, `Square`, `Circle`, `Line`, `Polygon`, `PolyLine`, `FreeText`, `Stamp`):**
     - `ReadAnnotCapturedPaths` recursively traverses top-level `/AP` objects and nested `FPDF_PAGEOBJ_FORM` (`5`) Form XObjects (`FPDFFormObj_CountObjects` / `FPDFFormObj_GetObject`), composing affine matrices (`MultiplyMatrix`) and discarding transparent (`sa == 0` / `fa == 0`) hit-test paths.
     - `ReadAnnotColor` resolves the true visual color from captured vector paths first, then `FPDFAnnot_GetColor`, then raw `/AP` content stream operators (`RG`/`rg`, `G`/`g`, `K`/`k` via `FPDFAnnot_GetAP`), and finally subtype defaults.
     - Non-markup annotations can be dragged across the page (`AnnotationMoved` + live `AnnotationDragGhostBorder`) and copied/cut/pasted (`Ctrl+C`, `Ctrl+X`, `Ctrl+V`). Pasting anchors the annotation's top-left corner at the last mouse click position (`PdfPageCanvas.MouseClickedOnPage`, context menu target, or QuickBar light-dismiss cursor position).
2. **Annotation Floating UI & 6 Preset Colors (`DocumentView.xaml(.cs)`):**
   - 6 preset colors: Yellow (`#FFFFE620`), Green (`#FF4CD964`), Blue (`#FF4DA6FF`), Pink (`#FFFF66B2`), Orange (`#FFFF9F40`), and Red (`#FFF24C4C`). Selecting a color in any toolbar or selection menu updates `DefaultAnnotationColor` / `ActiveAnnotationColor` without prematurely creating an annotation.
   - `MouseSelectionFlyout`: compact horizontal icon bar + 6 color swatches shown on right-click when text is selected.
   - `AnnotationQuickBarFlyout`: compact Acrylic icon bar offset `20 DIPs` above/below the selected annotation (preventing accidental deletion on double-click/double-tap).
   - `AnnotationEditorFlyout`: Acrylic comment editor with a single top-right `×` close button, Save + Delete buttons on the bottom-left, and 6 color swatches on the bottom-right.
3. **Zero-Flicker Sidebar & 40-Step Undo (`DocumentViewModel.cs`, `AnnotationsTabPage.xaml`):**
   - `RebuildAnnotationsSummary` updates `AnnotationItemViewModel` properties in place (`[ObservableProperty]`) when moving or recoloring annotations, and inserts/removes items individually so `AddDeleteThemeTransition` and `RepositionThemeTransition` animate smoothly without list resets.
   - `_annotationUndoStack` records up to 40 `AnnotationUndoRecord` snapshots (`Created`, `Deleted`, `Modified`) for `Ctrl+Z` (`UndoAnnotationAsync`).
4. **Digital Signatures (`PdfSignatureInspector.cs`, `DocumentPropertiesDialog.xaml(.cs)`):**
   - Inspects all signature fields via `FPDF_GetSignatureCount` and `FPDFSigObj_*`, verifying CMS/PKCS#7 byte ranges and X.509 chains in a provider-agnostic manner.
   - Signatures with intact cryptographic integrity whose root CA is not installed in the local Windows trust store are treated as valid integrity (`IsTrusted = true`, green checkmark in the status bar badge) with an explanatory note in the unified **Document Properties** dialog (`Alt+Enter` or clicking the status bar file size / signature button).

### 4.6 Remaining Roadmap for `v0.6.x` and `v0.7.x` **(design)**

#### Remaining for `v0.6.x` (`v0.6.1+` — Freehand Ink, Eraser & Annotation Settings)
- **Interactive Freehand Pen / Ink (`AnnotationTool.Pen`) & Eraser (`AnnotationTool.Eraser`):**
  - Zero-latency Win2D wet-ink preview stroke on `PdfPageCanvas` during `PointerMoved` (respecting `IsTouchDrawingEnabled` to separate finger pan/zoom from stylus/mouse drawing), committing `FPDF_ANNOT_INK` (`FPDFAnnot_AddInkStroke` + `/AP` stream) on `PointerReleased`.
  - Interactive `AnnotationTool.Eraser` to delete ink strokes or annotations on click/scrub.
  - Redo stack (`Ctrl+Y`) + Undo/Redo toolbar buttons.
- **In-Place Save & Annotation Preferences (`local-todo.md`):**
  - Direct save (`Ctrl+S`) + unsaved-changes confirmation prompt (`HasUnsavedChanges`) on close, plus optional auto-save setting.
  - Settings toggles: *"Keep annotation tool active after creation"*, pen multi-stroke grouping timeout, ink smoothing, and Stylus mode (Pen vs. Hand).
  - Fix active-page thumbnail border in `ThumbnailsTabPage` (sharp rectangular border matching exact page bounds without rounded corner overflow).

#### Planned for `v0.7.x` (Multi-Document Tabbed Shell & Platform Integration)
- **Multi-Document Tabbed Architecture (`v0.7.0`):**
  - **Architectural Readiness (Why `v0.6.x` was built per-document):** `DocumentViewModel` is already a self-contained per-document state container (`DocumentId`, `Pages`, `CurrentPageIndex`, `ZoomFactor`, `IsContinuous`, `IsDoublePage`, `LastVerticalOffset`, `LastHorizontalOffset`, search state, `AnnotationsSummary`, `_annotationUndoStack`, and `Signatures`), while `TileCache` and `PdfiumPageCache` are already keyed by `(documentId, pageIndex, ...)`.
  - `ShellViewModel.OpenDocuments` (`ObservableCollection<DocumentViewModel>`) + active `CurrentDocument` switching with full per-tab state isolation (page, zoom, view mode, scroll offsets, search, sidebar, and undo/redo history).
  - Native WinUI 3 `TabView` in `MainWindow.xaml` with dirty indicator (`•` when `HasUnsavedChanges`), `+` button, middle-click close, `Ctrl+T`, `Ctrl+W`, `Ctrl+Tab` / `Ctrl+Shift+Tab`, multi-file open/drop, and session restore in `AppSettings`.
  - Inactive tab RAM suspension / disk hibernation to keep memory footprint flat across many open tabs, plus deterministic per-tab disposal (`CloseDocumentAsync`, `_tileCache.RemoveDocument(docId)`).
  - Single-instance `.pdf` file activation routing (`AppInstance.FindOrRegisterForKey`), Windows Share menu, Taskbar Jump List (recent files), attachment export (`FPDFAttachment_GetFile`), persistent user bookmarks (`bookmarks.json`), basic AcroForms filling, diagnostic log button in Settings, beta language selector, GitHub Releases auto-updater, and `.msix` Store packaging.

---

## 5. Coordinate systems and transformation matrices

| Space | Unit | Origin | Y axis |
|-------|------|--------|--------|
| **PDF user space** | point = 1/72 in | bottom-left of MediaBox (CropBox may be offset) | up |
| **Page DIP space** | DIP = 1/96 in | top-left of the visible (cropped) page | down |
| **Content DIP space** | DIP | top-left of the ScrollViewer content (all pages laid out) | down |
| **Viewport DIP space** | DIP | top-left of the ScrollViewer viewport | down |
| **Physical pixels** | px | top-left of viewport | down |

`PageCoordinateTransform` provides `ToPage(Vector2)`, `ToPdf(Vector2)`, `ToPageRect(PdfRect)`, and
`ToPdfRect(Rect)` accounting for `CropBox ∩ MediaBox` origin offsets (`OriginXPt`, `OriginYPt`),
intrinsic page `/Rotate`, and user view rotation (`0°`, `90°`, `180°`, `270°`).

---

## 6. Internationalization

- Resources: `src/WinPDF/Strings/<lang>/Resources.resw`, MRT Core (`Microsoft.Windows.ApplicationModel.Resources`).
- **`en-US` is the default/fallback** (`<DefaultLanguage>` in the csproj). `pt-BR` must have
  **exactly the same keys**. Run `pwsh tools/Check-Resources.ps1` before committing.

---

## 7. Build, run, verify

Prerequisites: Windows 10 19041+ (Windows 11 recommended for Mica), **.NET SDK 10.0.100+**.

```powershell
# Restore + build (x64 is the default platform; x86 / ARM64 also supported)
dotnet build WinPDF.sln -c Debug -p:Platform=x64

# Run (unpackaged, self-contained Windows App SDK)
.\src\WinPDF\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WinPDF.exe [path\to\file.pdf]

# Resource key parity check
pwsh .\tools\Check-Resources.ps1
```
