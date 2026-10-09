# Plan i stanje

Glavni pregled petlje arhitekt -> tester -> builder -> review -> merge. Redoslijed koraka dolazi iz
`docs/roadmap.md` (piše ga arhitekt); ovaj dokument vodi orkestrator i ažurira ga nakon svakog koraka.

Zadnje ažuriranje: 2026-10-09 09:05

## Koraci po specifikaciji

Svaki spec prolazi pet koraka:

1. **Spec** — arhitekt napiše `docs/specs/NNN-*.md` (i ADR ako treba).
2. **Testovi** — tester napiše black-box acceptance testove na grani `tests/NNN-*`.
3. **Kod** — builder implementira na grani `feat/NNN-*` dok svi testovi ne prođu.
4. **Review** — arhitekt napiše `docs/reviews/NNN.md` s presudom OK.
5. **Merge** — orkestrator spoji granu u `main` i pusha na GitHub.

Oznake: ✅ gotovo · 🔄 u tijeku · ⬜ nije započeto · — ne primjenjuje se

| # | Spec | Spec | Testovi | Kod | Review | Merge |
|---|---|:-:|:-:|:-:|:-:|:-:|
| 001 | Temelj + jedinice mjere (slojevi, tenanti, API ključevi, model grešaka) | ✅ | ✅ | ✅ | ✅ | ✅ |
| 002 | Artikli (matični podaci artikala) | ✅ | ✅ | ✅ | ✅ | ✅ |
| 003 | MCP server + upravljanje API ključevima | ✅ | ✅ | ✅ | 🔄 | ⬜ |
| 004 | Partneri i skladišta | ✅ | ✅ | 🔄 | ⬜ | ⬜ |
| 005 | Skladišna knjiga, primke i izdatnice | ✅ | ✅ | ⬜ | ⬜ | ⬜ |
| 006 | Međuskladišnice i storno | ✅ | 🔄 | ⬜ | ⬜ | ⬜ |
| 007 | Preračun jedinica mjere po artiklu | ✅ | ⬜ | ⬜ | ⬜ | ⬜ |
| 008 | Inventura / korekcija zaliha | ✅ | ⬜ | ⬜ | ⬜ | ⬜ |
| 009 | Narudžbe dobavljačima -> primka robe | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 010 | Prodajne narudžbe -> isporuka | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 011 | Vrednovanje zaliha | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 012 | Kontni plan + temeljnice | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 013 | Brojčane serije dokumenata | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 014 | Porezi + izlazni/ulazni računi | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 015 | Plaćanja i otvorene stavke | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 016 | Audit log *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 017 | Dozvole po API ključu *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 018 | OpenAPI dokument *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 019 | Row-level security *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 020 | Rate limiting *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |

Napomena: testove za 001 i 002 pisao je builder; od 003 nadalje piše ih tester.

## Što tko trenutno radi

- **Arhitekt:** review speca 003.
- **Builder:** implementacija speca 004 (grana `feat/004-partners-warehouses`).
- **Tester:** usklađivanje testova 004 s odgovorima arhitekta, zatim testovi za spec 006.

## Dnevnik

- 2026-10-08 — spec 001 napisan; builder implementirao; review tražio 2 ispravka (NUL znak u nazivu -> 500,
  nepoznati query parametri).
- 2026-10-09 — 001 i 002 prošli review, spojeni u `main` i pushani (`5f5687d`).
- 2026-10-09 — dodan tester kao treći agent (`tester.md`); builder od 003 radi nad testerovim testovima.
- 2026-10-09 — roadmap presložen: prvo poslovna logika (zalihe, nabava, prodaja, računovodstvo), platforma na kraju.
- 2026-10-09 — specovi 003–008 napisani; testovi za 003, 004, 005 napisani; kod za 003 gotov
  (172 unit + 376 integracijskih testova prolazi, provjerio orkestrator).

## Odluke vlasnika

Potvrđeno:
- Spec 003: nema MCP alata za kreiranje ključeva (samo HTTP); svaki ključ smije upravljati ključevima dok ne
  dođu dozvole; MCP klijenti samo sa statičkim API ključem; rate limiting kasnije.

Arhitektovi defaulti koji vrijede dok vlasnik ne kaže drugačije (detalji u ADR-ovima):
- Spec 004 (ADR-0011): porezni broj nije jedinstven; nema validacije po državama; jedna adresa po partneru;
  bez kontakt podataka; partner mora biti kupac ili dobavljač.
- Spec 005 (ADR-0012): negativna zaliha uvijek odbijena; provjera prema trenutnoj zalihi; fiksni brojevi
  `SR-`/`SI-`; količine do 6 decimala; ručne primke/izdatnice bez partnera i cijene; draft ne rezervira zalihu.
- Spec 006 (ADR-0013): prijenos je trenutan; storno je zaseban dokument iz iste serije, cijeli i konačan,
  odbijen ako bi zaliha otišla u minus; datum storna zadaje pozivatelj.
- Spec 007 (ADR-0014): vidi ADR.
- Spec 008 (ADR-0015): zastarjela inventura se odbija; inventura je djelomična; artikl jednom po inventuri;
  ništa se ne zamrzava dok je inventura otvorena; proknjižena inventura se može stornirati.
