# Translating Moss365

The interface of Moss365 comes from dictionaries, one per language (step R15 of the [roadmap](roadmap-1.0.md)):

| Language | File |
| --- | --- |
| English (the fallback) | `src/Moss365.Core/Localization/Texts/en.json` |
| Dutch | `src/Moss365.Core/Localization/Texts/nl.json` |
| Brazilian Portuguese | `src/Moss365.Core/Localization/Texts/pt-BR.json` |
| French | `src/Moss365.Core/Localization/Texts/fr.json` |
| German | `src/Moss365.Core/Localization/Texts/de.json` |

Every file has the same keys (`"menu.gebruikers": "Users"`). A text with values has numbered holes (`{0}`, `{1:dd/MM}`):
keep the same holes in every language. A missing or empty text falls back to English, so a button is never empty; the
tests report every missing translation.

## Which language the app shows

1. The choice in **Settings, Language** (per Windows user on this PC, stored in `moss365.db`).
2. Otherwise the display language of Windows (nl-NL and nl-BE give Dutch, pt-PT gives Brazilian Portuguese, ...).
3. Otherwise English.

Dates and numbers follow the language (`en-GB`, `nl-BE`, `pt-BR`, `fr-BE`, `de-DE`).

## What is not translated

Data from AD and Microsoft 365 (names of groups, departments, policies), the content of scripts and the change log
entries already written. That is the data of the organisation, not text of the app.

## Fixed terms

The same word everywhere for: plan, snapshot, tenant, role, change log (keys `term.*`). Use them in new texts.

## For contributors

- A screen takes its text with `@T["key"]` or `@T.Format("key", value)` (`UiText` in `Moss365.UI`). Add the key to all
  five files.
- Not every screen is translated yet. `tests/Moss365.Tests/Localization/untranslated.txt` lists, per screen, how many
  texts are still written in the screen. The test `NoNewHardCodedTextInTheScreens` fails when a screen gets more; when
  you move a screen to the dictionaries, lower its number or remove its line.
- A new language: add a `LanguageInfo` to `Translator.Languages` and a dictionary with every key.
