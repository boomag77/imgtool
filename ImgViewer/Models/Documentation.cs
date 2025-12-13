using System.Collections.Generic;

namespace ImgViewer.Models
{
    internal class Documentation
    {
        public IReadOnlyList<DocSection> Sections { get; }

        private Documentation(IReadOnlyList<DocSection> sections)
        {
            Sections = sections;
        }

        public static Documentation CreateDefault()
        {
            return new Documentation(BuildSections());
        }

        private static IReadOnlyList<DocSection> BuildSections()
        {
            return new[]
            {
                new DocSection(
                    "overview",
                    "Getting Started",
                    "Orient yourself in the workspace and learn how images flow through Image Genie.",
                    new[]
                    {
                        new DocParagraph(
                            "Workspace overview",
                            "The left preview always shows the untouched original while the right preview shows the current working image. "
                            + "When you load an image, the app keeps the original frozen so you can compare results at any time. "
                            + "Status text under the previews mirrors what the processing pipeline is doing (loading, deskewing, standby, etc.)."),
                        new DocParagraph(
                            "Recommended workflow",
                            "Work top to bottom: load → preview each operation with the Preview buttons → adjust parameters → save or batch-run. "
                            + "Each operation acts on the output of the previous one, so the order of operations in the pipeline list matters. "
                            + "Use Reset Image to revert the working preview to the original without reloading from disk."),
                        new DocParagraph(
                            "Preview tips",
                            "Parameters are debounced so you can drag sliders smoothly; the image refreshes after the short delay defined in Settings. "
                            + "If repeated adjustments feel stuck, press Cancel Processing to recreate the processor token and free system resources.")
                    }),
                new DocSection(
                    "deskew",
                    "Deskew",
                    "Straightens tilted scans so every other tool sees a square page.",
                    new[]
                    {
                        new DocParagraph(
                            "When to run",
                            "Always run Deskew first. A straight baseline makes border detection, binarization, and smart crop noticeably more reliable."),
                        new DocParagraph(
                            "Algorithm choice",
                            "Auto cycles through several heuristics and works for most paperwork. "
                            + "By Borders relies on clearly visible page edges; lower Canny Threshold 1 (35–60) to pick up faint borders and keep Threshold 2 higher (120–200) to ignore background noise. "
                            + "Raise Morph Kernel (3–7) only when stains break the detected edges. "
                            + "Hough looks for ruler-length lines; increase Min Line Length toward 60% of page width to ignore short scribbles, and raise Hough Threshold if random diagonals get detected. "
                            + "Projection and PCA are quick fallbacks when text baselines are strong—you do not need extra parameters there."),
                        new DocParagraph(
                            "Practical advice",
                            "If you increase either Canny threshold and the page stops rotating, return them to their defaults before switching algorithms. "
                            + "For blueprint-like drawings with long horizontal rulers, Hough + high Min Line Length gives the most repeatable result.")
                    }),
                new DocSection(
                    "borders",
                    "Borders Remove",
                    "Trims dark scanner borders and fills the background so later steps only see content.",
                    new[]
                    {
                        new DocParagraph(
                            "Auto mode",
                            "Auto inspects connected components to decide what belongs to the border. "
                            + "Leave Auto Threshold on for most jobs; Margin % defines the safety cushion (raise to 12–15% if annotations sit near the edge). "
                            + "Shift factor controls how aggressively the trim shifts inward once text is detected—lower it for aggressive cropping, raise it to protect edge notes. "
                            + "Disable Auto Threshold only when the background is uneven, then set Dark Threshold to the measured paper value (30–60 for off-white) and lock Background Color close to the paper tone. "
                            + "Increase Min Area Px, Span fraction, Solidity, or Min depth fraction when heavy drop shadows mimic real borders. "
                            + "Feather softens the cut (4–8 px by default); higher values blend filled areas but might reintroduce noise. "
                            + "Use Telea Hybrid keeps fill seamless; turn it off when you intend to crop instead of inpaint."),
                        new DocParagraph(
                            "By contrast and manual cuts",
                            "By Contrast excels when borders are visibly darker: Threshold Fraction (0.35–0.45) controls how dark the edge has to be, Contrast Threshold tunes sensitivity, "
                            + "Central Sample determines which middle area is preserved, and Max remove frac caps how much margin disappears. "
                            + "Manual offsets give full control: enter pixel amounts for each side, enable Cut to chop or preview to draw red guides, "
                            + "and use Apply to Left/Right Page to choose which half of a spread the offsets touch (at least one side stays selected so nothing vanishes)."),
                        new DocParagraph(
                            "Integral mode",
                            "Integral mode scans the entire edge band. Increase Seed contrast/brightness strictness (1.2–1.8) when the paper texture varies, raise Texture allowance to ignore subtle patterns, "
                            + "and enlarge Scan step for faster processing on large batches. "
                            + "Inpaint radius defines the cleanup footprint, while Inpaint mode picks the algorithm (Fill keeps it simple, Telea/NS follow OpenCV behavior). "
                            + "Auto max border depth automatically sets how far each side can cut; disable it only if you need different left/right values and then keep each depth fraction below ~0.3 to avoid trimming text.")
                    }),
                new DocSection(
                    "binarize",
                    "Binarize",
                    "Converts the page to crisp black and white for archival, OCR, or fax-like exports.",
                    new[]
                    {
                        new DocParagraph(
                            "Threshold and Majority",
                            "Threshold is the fastest method: move the slider upward to lighten the background (risk losing pencil), move downward to keep faint ink (risk a gray page). "
                            + "Majority shifts the final vote; use a positive Majority Offset (+10 to +30) to darken everything uniformly or a negative value to lighten. "
                            + "If you make big moves on Threshold, adjust Majority Offset in the same direction but with smaller magnitude to avoid posterization."),
                        new DocParagraph(
                            "Adaptive + morphology",
                            "Adaptive reacts to local lighting. BlockSize must stay odd; smaller blocks (11–21) hug tiny details, larger blocks (31–41) smooth gradients. "
                            + "Mean C subtracts a bias: increasing it forces the threshold higher (darker result), decreasing it keeps paper brighter. "
                            + "Use Gaussian softens harsh transitions when stains cause halos. "
                            + "Enable Apply Morphology when letters break apart: Morph kernel (odd number) picks the neighborhood, while Morph iterations determines how often it runs. "
                            + "Pair a larger kernel with fewer iterations or vice versa to avoid closing loops entirely."),
                        new DocParagraph(
                            "Sauvola specifics",
                            "Sauvola excels on faint pencil. Window size should cover the stroke thickness (25–35 for notebooks), K (0.3–0.4) controls how much local contrast shifts the cut, "
                            + "and R (180–240) represents expected intensity range. "
                            + "Pencil stroke boost re-injects contrast into barely visible strokes; increase it gradually and lower K if background noise starts reappearing. "
                            + "Use CLAHE when gradients are severe: CLAHE Clip caps amplification (2–4 for documents), and Grid size controls region size (smaller = more detail). "
                            + "Morph radius gently erodes isolated noise—leave it at 0 unless thick halos remain.")
                    }),
                new DocSection(
                    "punchholes",
                    "Punch Holes Remove",
                    "Erases binder holes without touching nearby stamps or seals.",
                    new[]
                    {
                        new DocParagraph(
                            "Shape selection",
                            "Set Punch Shape to match your pages: Circle for round binders, Rect for square perforations, Both for mixed sets. "
                            + "Diameter/Width/Height should match the measured pixel size; if holes vary, raise Size tolerance (0.6–0.9) so they remain eligible."),
                        new DocParagraph(
                            "Appearance controls",
                            "Roundness (0–1) filters oblong blobs—keep it above 0.85 for classic holes and lower it for stretched tears. "
                            + "Fill ratio describes how solid the hole appears; lower values allow partially filled spots, higher values demand perfect voids. "
                            + "Density tells the detector how dark the hole should be (1.0 = jet black). "
                            + "Offsets fence the search area so logos near the edge stay intact; set them roughly to your margin width minus a small buffer (for example, 100 px).")
                    }),
                new DocSection(
                    "despeckle",
                    "Despeckle",
                    "Removes tiny dust and toner flakes without softening real strokes.",
                    new[]
                    {
                        new DocParagraph(
                            "Area controls",
                            "With Small area relative enabled, Small area multiplier (0.3–0.7) scales the target spot size to the page. "
                            + "Disable the flag to switch to Small area absolute px and set the largest pixel count you want removed (for example, 50 px). "
                            + "Max dot height fraction and Proximity radius fraction define how tall and how close specks can be—raise them to target slightly larger blotches."),
                        new DocParagraph(
                            "Shape and clustering",
                            "Squareness tolerance controls how irregular a candidate can be (0.6 keeps circular dots, higher values catch elongated ink). "
                            + "Keep clusters preserves groups of dots when they look like stippling; disable it only when you need surgical-level cleanliness. "
                            + "Use dilate before CC melds fragmented dirt: pick a kernel orientation plus Dilate iterations (start with 1). "
                            + "Size tolerance lets each candidate deviate from the average; raise it if some specks are larger yet still considered noise. "
                            + "Enable Show candidates to preview removals before applying them to the working image.")
                    }),
                new DocSection(
                    "lines",
                    "Lines Remove",
                    "Targets ruled, form, or table lines without harming handwriting.",
                    new[]
                    {
                        new DocParagraph(
                            "Orientation and width",
                            "Choose Orientation (Horizontal, Vertical, or Both) to match the ruling direction. "
                            + "Line width (px) should equal the observed stroke thickness; too-small values leave ghost lines, too-large values can shave off borders."),
                        new DocParagraph(
                            "Length and color filters",
                            "Min length fraction expresses how much of the page width/height the line must cover—set it above 0.6 when you only want long guides removed. "
                            + "Offset start skips a header/footer area measured from the top. "
                            + "Specify Line color (RGB) to target colored forms; leave them at -1 to let the tool auto-pick the darkest strokes. "
                            + "Color tolerance (20–40) allows for faded ink; increasing it gradually prevents accidental removal of stamps or signatures.")
                    }),
                new DocSection(
                    "smartcrop",
                    "Smart Crop",
                    "Automatically crops to content after cleanup.",
                    new[]
                    {
                        new DocParagraph(
                            "U-net mode",
                            "U-net focuses on overall document contours. Crop level (0–100) slides from conservative to aggressive trimming: raise it toward 70 for tight crops, "
                            + "lower it near 55 when you want to preserve marginalia. Always preview before committing, especially on multi-page spreads."),
                        new DocParagraph(
                            "EAST mode",
                            "EAST detects text regions. Preset (Fast, Balance, Quality) seeds typical width/height/threshold combinations. "
                            + "Input width/height control neural-net resolution—larger values produce more accurate boxes but take longer. "
                            + "Score threshold filters weak detections (lower it to catch faint handwriting, raise it to ignore noise), "
                            + "and NMS threshold merges overlapping boxes (lower numbers reduce duplicate boxes). "
                            + "Tesseract min confidence discards boxes where OCR is unsure (set 45–60 for older scans). "
                            + "Padding px adds breathing room, Downscale max width limits the size processed for performance, "
                            + "Include handwritten and Include stamps toggle specialized detection, and Handwritten sensitivity decides how much faint pen affects the crop. "
                            + "Turn off East debug when you no longer need overlays for a speed boost.")
                    }),
                new DocSection(
                    "playbooks",
                    "Combining Operations",
                    "Suggested sequences help you get consistent results on common document types.",
                    new[]
                    {
                        new DocParagraph(
                            "Office paperwork",
                            "Deskew (Auto) → Borders Remove (Auto with Telea) → Binarize (Adaptive with morphology) → Despeckle (relative 0.4) → Lines Remove (Horizontal) → Smart Crop (U-net, crop level 60). "
                            + "Export as CCITT G4 TIFF for tiny files."),
                        new DocParagraph(
                            "Antique books",
                            "Deskew (By Borders with low Canny thresholds) → Borders Remove (Manual bottom shadow trim) → Smart Crop (U-net level 55) → Punch Holes Remove (Circle) → "
                            + "Binarize (Sauvola + CLAHE) → Despeckle (absolute 80 px). Save the pipeline JSON and reuse it on entire folders."),
                        new DocParagraph(
                            "Colored forms",
                            "Deskew → Binarize (Threshold to keep colors) → Lines Remove (set RGB to grid color, orientation Both) → Borders Remove (By Contrast) → Despeckle (relative 0.3). "
                            + "If lines overlap text, lower Min length fraction so only the longest guides disappear."),
                        new DocParagraph(
                            "General advice",
                            "Carry the preview workflow through each operation; adjust one parameter at a time and observe how downstream steps react. "
                            + "When you change a slider drastically (for example, large Morph kernel), revisit earlier steps such as Borders Remove so you do not accidentally clip content after the fact.")
                    })
            };
        }
    }

    internal class DocSection
    {
        public DocSection(string id, string title, string summary, IReadOnlyList<DocParagraph> paragraphs)
        {
            Id = id;
            Title = title;
            Summary = summary;
            Paragraphs = paragraphs;
        }

        public string Id { get; }
        public string Title { get; }
        public string Summary { get; }
        public IReadOnlyList<DocParagraph> Paragraphs { get; }
    }

    internal class DocParagraph
    {
        public DocParagraph(string heading, string body)
        {
            Heading = heading;
            Body = body;
        }

        public string Heading { get; }
        public string Body { get; }
    }
}
