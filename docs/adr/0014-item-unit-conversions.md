# ADR-0014: Unit conversions belong to the article; documents keep what was entered, the ledger keeps base units

Status: accepted (2026-10-09) — continues ADR-0008 and ADR-0012 (decision 8); first applied by spec 007.
Points 1, 4, 5, 6 and 7 are the architect's defaults and are listed for the owner in the hand-over of spec 007.

## Context
Goods are bought by the box and stocked by the piece. ADR-0012 fixed that the ledger and stock on hand are in
the article's base unit and left "a line cannot choose a unit" open until this spec. Open questions: where a
conversion lives, what a document line stores, what happens when a factor is corrected later, and what to do
when a converted quantity is not exact.

## Decision
1. **A conversion belongs to one article**: (article, unit) -> `factor`, meaning *one of this unit is `factor`
   base units of this article*. There are no global conversions between units and no unit categories: a box is
   12 pieces of one article and 50 of another.
2. **The base unit is always usable and has no conversion row**; its factor is 1 by definition. A conversion
   for the article's own base unit is refused.
3. **The ledger and stock on hand stay in base units**, without exception. Sufficiency, conservation and net
   zero (ADR-0012, ADR-0013) are stated and checked on base quantities.
4. **A document line stores what the user entered** — unit and quantity — **and, once posted, the factor used
   and the resulting base quantity.** A draft is converted with the article's *current* factor every time it is
   read and when it is posted; posting freezes factor and base quantity on the line for good.
5. **A factor may be changed at any time.** It changes drafts and future documents only. Posted lines, the
   ledger and reversals (which copy the original's lines and negate its entries) are unaffected.
6. **Conversion rounds to 6 decimal places, half away from zero**, and is refused
   (`409 QUANTITY_NOT_CONVERTIBLE`) when the result would be zero or exceed the quantity maximum. There is no
   rounding precision per unit.
7. **While an article has conversions, its base unit is frozen** (`409 IN_USE`, as for a used article): the
   factors are meaningless against another base unit. Delete the conversions first.
8. **A conversion is addressed by its two ids** (`/articles/{articleId}/units/{unitId}`) and written by an
   idempotent `PUT` that creates or replaces. It has no id or code of its own.
9. **A conversion used by a draft line cannot be deleted** (`409 IN_USE`); one used only by posted lines can —
   they carry their own factor. The unit of measure itself stays `IN_USE` while any line, draft or posted,
   or any conversion names it.

## Alternatives
- **Global conversions within a unit category** (Odoo: every unit has a ratio to the category's reference
  unit). Right for physical units (g/kg), wrong for packaging, which is what small traders actually need and
  which differs per article. Physical ratios can be entered per article with the same mechanism.
- **Alternative units as part of the article body** (`"units": [...]` on create and replace). One request, but
  it changes the article contract of spec 002 for every client and makes "replace all six fields" replace the
  conversions too. Rejected: a sub-resource leaves the article untouched.
- **Freeze the factor once a conversion is used** (as base unit and type are frozen). Protects nothing — the
  ledger holds base quantities — and leaves a mistyped factor uncorrectable. Rejected.
- **Convert a draft once, when it is saved.** Then a factor corrected between saving and posting silently
  posts the old value, or the draft must be re-saved for no visible reason. Posting already re-reads master
  data (ADR-0012, decision 10); the factor follows the same rule.
- **Refuse inexact conversions instead of rounding.** Honest, but it refuses ordinary entries (1.125 lb at
  0.453592 kg) for a difference below the sixth decimal. BC and ERPNext both round. Rejected.
- **Rounding precision per article unit** (BC "Qty. Rounding Precision", e.g. whole pieces only). Useful to
  stop "0.5 pieces"; it is a restriction on quantities in general, not on conversion, and applies to base
  units as well. Deferred.
- **Default purchase and sales units on the article** (BC, Odoo). Belongs to the specs that have purchase and
  sales lines (009, 010).

## Consequences
- Every later document with quantity lines (purchase 009, sales 010) uses the same line fields — `unitId`
  in, `unit`, `quantity`, `factor`, `baseUnit`, `baseQuantity` out — and the same conversion rule.
- A factor that cannot be an exact decimal (1/12) is stored rounded; choosing the smallest unit as base unit
  avoids it. Tool descriptions say so.
- Three error codes are added: `UNIT_IS_BASE_UNIT`, `UNIT_NOT_ON_ARTICLE`, `QUANTITY_NOT_CONVERTIBLE`.
- Valuation (011) must value base quantities; a price per entered unit is converted with the line's factor.
