# Approach and limitations

## The problem

A normal Selenium test finds a field with something like `By.Id("fname")` or an XPath such as
`//form/div[2]/input`. Both break as soon as an id is renamed, a field moves, or the markup is wrapped
in another `div`. The task was to fill First Name, Gender and State in a way that survives those changes,
and to avoid picking a similar-looking field by mistake.

## The idea: find fields the way a person does

A person never reads ids. They read the words next to a box ("First Name"), recognise a group of round
buttons by its options ("Male / Female"), and pick the dropdown that contains the value they need. The
program does the same thing.

For every input, radio group and dropdown on the page it gathers evidence and gives each piece a weight:

| Evidence | Weight |
|---|---|
| Real `<label>` linked to the field (`for=` or wrapping) | 1.00 |
| `aria-label` / `aria-labelledby` / `<fieldset><legend>` | 1.00 |
| Closest text before the field in the HTML (also through Shadow DOM slots) | 0.85 |
| Closest label-like text to the left of / above the field on screen | 0.75 |
| `placeholder` | 0.70 |
| `name`, `title`, `data-test*` attributes / `id` | 0.60 / 0.50 |

The text is compared with keyword lists from `appsettings.json` ("first name", "given name", "fname"…),
ignoring case and punctuation and matching whole words only. The best piece of evidence counts in full;
extra agreeing evidence adds a small bonus.

Three more rules make it hard to fool:

1. **Negative keywords.** Words such as "last", "surname", "father", "nickname", "user", "shipping" or
   "country" subtract points, so "Last Name", "Father's First Name" or "Shipping State" lose to the real
   field.
2. **Type-specific evidence.** A radio group whose options look like genders gets a bonus even with no
   "Gender" label. A dropdown must actually contain the requested option. Options are matched exactly
   first, so "Female" can never match "Male". The program also works out whether option text is written
   after each radio (usual) or before it.
3. **Pick the whole form, not single fields.** The live page has a second, almost identical form inside
   Shadow DOM. The program groups candidates by their `<form>` (or page/shadow root), keeps only groups
   that contain all three fields, and ranks them on visible evidence only, because ids and names are
   exactly what change between versions. If two forms are within 10% of each other, the one higher on
   the page wins.

## How the code is split

- `Locator/SmartLocator.js` is the scoring engine. Selenium runs it inside the page in one call
  (`IJavaScriptExecutor`), so even large pages are scored in about 0.1 s instead of hundreds of
  round-trips. It returns the chosen elements plus a readable list of reasons.
- `Locator/SmartFormLocator.cs` waits for the page, retries until a timeout (for late-loading forms),
  and searches iframes up to three levels deep.
- `Actions/FieldActions.cs` types, clicks and selects with normal Selenium actions. If a styled widget
  blocks a click, it clicks the option's label, then falls back to a script. Every action is verified by
  reading the value back from the page. If the page re-renders mid-test, the field is located again.
- Everything that might need tuning (URL, browser, test values, keyword lists, confidence threshold)
  lives in `appsettings.json`, environment variables or command-line options.

## How it was tested

`TestPages/` holds 13 modified versions of the form: reordered fields, meaningless ids and classes,
table and CSS-grid layouts, styled radios, aria-only labels, decoy forms and look-alike fields, an
iframe, a form injected after 2 s with random ids on every load, a page with no `<form>`, a Shadow DOM
form, and the original page stripped of every id/name/class with its Shadow DOM duplicate left in place.
Each page marks the correct element with `data-expect`, which the program never reads; `--suite` mode
checks it afterwards to prove the right element was used. All 13 pass, and so does the live page.

## Limitations

- **Two truly identical forms.** If a page has two forms with the same labels and fields, the program
  cannot know which one is "real" and uses the first one on the page.
- **Custom dropdowns.** Only native `<select>` elements are handled. A dropdown built from `div`s and
  JavaScript (e.g. some React/Material components) would need an extra strategy.
- **Closed Shadow DOM and cross-origin iframes** cannot be read by any script, so fields inside them are
  not found.
- **Wording and language.** Matching depends on the keyword lists. A label like "What should we call
  you?" or a non-English page needs new keywords in `appsettings.json` (no code change).
- **No labels at all.** If a field has no label, placeholder, aria text or nearby text, only its
  attributes are left, and the confidence may fall below the threshold. The program then fails clearly
  and lists the closest candidates instead of guessing.
- **Heuristic weights.** The weights were tuned on the live page and the 13 variants. A very unusual
  layout could need the threshold or weights adjusted.
