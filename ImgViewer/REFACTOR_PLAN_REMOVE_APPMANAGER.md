# Refactoring Plan: Remove IAppManager from OpenCvImageProcessor

## Goal
Decouple `OpenCvImageProcessor` from `IAppManager` by replacing direct calls with events.
This is the first step toward extracting the processor into a standalone DLL.

## Context

### Current coupling (3 call sites only, all UI notifications)

| Location | Call | Purpose |
|---|---|---|
| `WorkingImage` setter ~line 63 | `_appManager.SetBmpImageOnPreview(BitmapSource)` | Update preview on every image change |
| `ExecutePageSplitPreview` ~line 3020 | `_appManager.SetSplitPreviewImages(left, right)` | Show split page preview |
| `ExecutePageSplitPreview` ~lines 3024, 3037, 3041 | `_appManager.ClearSplitPreviewImages()` | Clear split preview on failure/cancel |

### Existing events in IImageProcessor (dead code)
- `event Action<Stream> ImageUpdated` — declared but never raised, never subscribed
- `event Action<string> ErrorOccured` — works correctly, this is the target pattern

### Principle
**Add new → verify works → remove old → commit**
Never remove old code until new path is wired up end-to-end.
Every step = working app = committable.

---

## Steps

### Step A — Add Split events to IImageProcessor interface
**File:** `Interfaces/IImageProcessor.cs`
Add declarations:
- `event Action<BitmapSource, BitmapSource>? SplitPreviewUpdated`
- `event Action? SplitPreviewCleared`

No implementation changes. App compiles and works unchanged.
**Commit:** `feat: add SplitPreview events to IImageProcessor interface`

---

### Step B — Raise Split events in processor (alongside old code)
**File:** `Models/OpenCVImageProcessor.cs`, method `ExecutePageSplitPreview`
Declare event fields. Next to each `_appManager` call, add `?.Invoke`:
- next to `_appManager.SetSplitPreviewImages(...)` → `SplitPreviewUpdated?.Invoke(...)`
- next to each `_appManager.ClearSplitPreviewImages()` → `SplitPreviewCleared?.Invoke()`

`_appManager` calls remain. Both paths run simultaneously.
**Commit:** `feat: raise SplitPreview events in OpenCvImageProcessor alongside appManager calls`

---

### Step C — Subscribe to Split events in AppManager
**File:** `Models/AppManager.cs`, where processor is created (~line 60)
Add subscriptions:
- `processor.SplitPreviewUpdated += (l, r) => SetSplitPreviewImages(l, r)`
- `processor.SplitPreviewCleared += () => ClearSplitPreviewImages()`

Both paths active simultaneously — idempotent, UI unchanged.
**Commit:** `feat: subscribe to SplitPreview events in AppManager`

---

### Step D — Remove direct _appManager calls for Split
**File:** `Models/OpenCVImageProcessor.cs`, method `ExecutePageSplitPreview`
Delete:
- `_appManager.SetSplitPreviewImages(...)`
- all three `_appManager.ClearSplitPreviewImages()` calls
- `if (_appManager == null) return;` guard at method start

Split Preview now works only through events.
**Commit:** `refactor: remove appManager direct calls from ExecutePageSplitPreview`

---

### Step E — Raise ImageUpdated in processor (alongside old code)
**File:** `Models/OpenCVImageProcessor.cs`, `WorkingImage` setter (~line 63)
Next to `_appManager.SetBmpImageOnPreview(...)`, add Mat → Stream conversion and raise `ImageUpdated?.Invoke(stream)`.
Event already declared — just never raised. `_appManager` call remains.
**Commit:** `feat: raise ImageUpdated event in WorkingImage setter`

---

### Step F — Subscribe to ImageUpdated in AppManager
**File:** `Models/AppManager.cs`
Add subscription and helper method:
- `_imageProcessor.ImageUpdated += stream => SetBmpImageOnPreviewFromStream(stream)`
- Add `SetBmpImageOnPreviewFromStream(Stream)`: decode Stream → BitmapSource → call existing `SetBmpImageOnPreview`

Both paths active simultaneously — UI unchanged.
**Commit:** `feat: subscribe to ImageUpdated event in AppManager`

---

### Step G — Remove direct _appManager call for main preview
**File:** `Models/OpenCVImageProcessor.cs`, `WorkingImage` setter (~line 63)
Delete:
- `_appManager.SetBmpImageOnPreview(...)`
- `if (_appManager == null) { previewSnap.Dispose(); return; }` (~line 60)
- `if (_appManager != null) previewSnap = value.Clone();` (~line 56) — simplify logic

Preview now works only through `ImageUpdated` event.
**Commit:** `refactor: remove appManager direct calls from WorkingImage setter`

---

### Step H — Remove _appManager from processor entirely
**File:** `Models/OpenCVImageProcessor.cs`
Delete:
- field `_appManager`
- constructor parameter `IAppManager appManager`
- `_appManager = appManager` initialization
- branch `if (appManager == null) → cvNumThreads = 0` — extract logic separately if needed

**File:** `Models/AppManager.cs`
Remove `this` from processor constructor call.

`OpenCvImageProcessor` no longer references `IAppManager` anywhere.
**Commit:** `refactor: remove IAppManager dependency from OpenCvImageProcessor`

---

### Step I — Fix tests
**File:** `ImgViewer.Tests/`
Find all `new OpenCvImageProcessor(...)` calls, remove the `IAppManager` argument (null or mock).
**Commit:** `test: update OpenCvImageProcessor instantiation after IAppManager removal`

---

## Step map

```
A  add events to interface                    → compiles, unchanged
B  raise events in processor (+ old code)    → both paths active
C  subscribe in AppManager (+ old code)      → both paths active
D  remove old Split calls                    → Split via events only
E  raise ImageUpdated (+ old code)           → both paths active
F  subscribe to ImageUpdated (+ old code)    → both paths active
G  remove old preview call                   → Preview via events only
H  delete _appManager entirely               → goal achieved
I  fix tests                                 → clean
```
