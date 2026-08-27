```
  /l、
（＾､＾７
  l  ~ヽ
  じしf_,)ノ
```

# gatto-extensions

the vetted extensions for gatto.

`C# script` · `GPL-3.0` · [gatto](https://github.com/bitlamas/gatto) · [the shelf](https://gatto.computer/extensions/) · [write your own](https://gatto.computer/extensions/write/)

Each extension is a single C# script that gives gatto a new tool. Everything here was read line
by line by a person before it went up, and that reading is what the listing means.

Copyright (C) 2026 bitlamas

---

## what is here

| file | tools it registers | what it does |
|---|---|---|
| `ask_user.csx` | `ask_user` | Stops and asks you 1 to 4 multiple-choice questions when gatto needs a decision it cannot make itself. |
| `web_search.csx` | `web_search`, `web_fetch` | Searches the web and fetches pages as readable text. Providers are configured in `gatto.json`. |

These two also ship **inside** gatto. That is not duplication; see *byte identity* below, which is
the reason this repository exists in the shape it does.

---

## installing one

Save the `.csx` file into your extensions folder, keeping its name:

```powershell
~\.gatto\extensions\ask_user.csx
```

Restart gatto. That is the whole install.

> **Keep the flat filename. Do not put it in a folder.**
>
> gatto discovers both `extensions/<name>.csx` and `extensions/<name>/main.csx`, so a folder
> *works*, but it is not the same extension as far as vetting is concerned. The content hash feeds
> each file's path before its bytes, so identical script text hashes differently:
>
> ```
> ask_user.csx  ->  71f381baac1c5a2ee2a377eb308aa431efecd92393af0123a20e5a48da3fb5bf
> main.csx      ->  aed94b7be60f7f5eec076d660c4028f5232091ca2d129b04da13e9aca11ab25a
> ```
>
> Only the flat form matches the hash compiled into gatto, and only a matching hash is vetted.
> Measured, not assumed: those are the same bytes under two names.

---

## `index.json`

The catalogue. It is the file consumers read; the repository layout is not an API.

```json
{
  "schema": 1,
  "extensions": [
    {
      "name": "ask_user",
      "file": "ask_user.csx",
      "version": 2,
      "description": "one line, for a person, not for the model",
      "tools": ["ask_user"],
      "sha256": "d84cb1e4...",
      "bytes": 6513
    }
  ]
}
```

| field | meaning |
|---|---|
| `schema` | catalogue format version. Bump only on a breaking change to these fields. |
| `name` | the extension's name. Matches `file` without the extension. |
| `file` | the script, at the repository root. Flat, always. |
| `version` | integer, bumped whenever `file` changes. A reader can compare it without hashing. |
| `description` | one line, written for a human reading a list. The model-facing description lives in the script's own `Gatto.Register` call and is deliberately not copied here. |
| `tools` | every tool name the script registers. Usually one; `web_search.csx` registers two, which is why this is a list. |
| `sha256` | plain SHA-256 of the file's bytes, so a download can be checked with any tool. **Not** gatto's vetting hash: that one folds newlines out and mixes in the path. Use this to check the file arrived intact, not to predict whether it will be vetted. |
| `bytes` | the file's size, as a second cheap integrity signal. |

Check a download:

```powershell
Get-FileHash ask_user.csx -Algorithm SHA256
```

---

## byte identity

gatto grants an extension the **read class** only when it is *vetted*, and vetting is a
content-hash match against the copy gatto ships. `ask_user` needs that grant to do its job.

So if a file here and the copy inside gatto differ by one byte, a stray newline or a helpful
reformat, someone who follows the install above gets an extension that loads **unvetted and
silently less privileged** than the one already bundled with their gatto. Nothing errors. It just
quietly does less.

**Therefore:**

1. These files are not edited here. gatto produces them and they are copied out.
2. A change goes into gatto first and arrives here as its output.
3. `version`, `sha256` and `bytes` are updated in the same commit as the file.

Line endings are safe: the content hash normalises CRLF and lone CR to LF before hashing, so a
Windows checkout stays vetted. Nothing else is safe, which is what the rules above are for.
`.gitattributes` pins `* -text`, so what you read here is what you get.

---

## contributing

Open a pull request. Say what the extension does and why it needs the access it asks for.

You never need anyone's permission to run your own extension. It is your folder.
