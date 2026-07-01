# Contributing to Talkty

Thanks for your interest. Talkty is a small, focused app, and contributions that
keep it that way are very welcome.

## Ground rules

- **Local-first stays the default.** Anything that sends audio or text off the
  device must be opt-in, off by default, and clearly labeled. No always-on network
  calls, no telemetry.
- **Keep it simple.** This is a tool people reach for fifty times a day. Speed and
  reliability beat features.
- **Match the surrounding style.** The code is MVVM with services behind interfaces.
  Read a nearby file before adding a new one.

## Getting set up

Requirements: .NET 8 SDK on Windows 10 or 11.

```bash
git clone https://github.com/v2matosevic/Talkty.git
cd Talkty/Talkty.App
dotnet build
dotnet run
```

Run the tests from the repo root:

```bash
dotnet test
```

The tests cover the text post-processing pipeline (replacements, hallucination
stripping, segment joining, the Prompting completeness guard). If you touch that
code, add or update a test.

## Making a change

1. Open an issue first for anything non-trivial, so we can agree on the approach
   before you spend time on it.
2. Branch, make the change, keep commits tight (what changed and why).
3. Run `dotnet build` and `dotnet test`.
4. Open a pull request using the template.

## Good first areas

- Post-processing rules and vocabulary defaults (`Services/TextPostProcessor.cs`,
  `Models/DefaultVocabulary.cs`).
- New language defaults and UI copy.
- Accessibility and keyboard handling in the settings window.

## A note on the architecture

There is no dependency injection container. Services are wired by hand in
`MainWindow.xaml.cs`. The model profile enum is persisted by number, so never
reorder or remove its members. [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md) documents
the gotchas in detail.
