```
  /l、
（＾､＾７
  l  ~ヽ
  じしf_,)ノ
```

# gatto-extensions

Extensions for [gatto](https://github.com/bitlamas/gatto). One C# script per extension. Each script registers one or more tools.

`C# script` · `GPL-3.0` · [the shelf](https://gatto.computer/extensions/) · [write your own](https://gatto.computer/extensions/write/)

Copyright (C) 2026 bitlamas

## Catalogue

| file | tools | class | description |
|---|---|---|---|
| `ask_user.csx` | `ask_user` | bundled, vetted | Asks 1 to 4 multiple-choice questions when gatto needs a decision. |
| `web_search.csx` | `web_search`, `web_fetch` | bundled, vetted | Searches the web and fetches pages as text. Providers: `gatto.json`. |
| `tasks.csx` | `task_read`, `task_write` | opt-in, not vetted | Reads and writes a task store through the store's own rules. |

## Classes

| class | ships inside gatto | read line by line by a person | permission prompt on use | hash checked by gatto |
|---|---|---|---|---|
| bundled, vetted | yes | yes | no | yes, against the bundled copy |
| opt-in, not vetted | no | no | yes, on every tool | no |

## Install

1. Copy the `.csx` file to `~\.gatto\extensions\<name>.csx`. Keep the file name. Do not put it in a folder.
2. Restart gatto.

Folder layout is not equivalent. gatto hashes the path with the bytes, so `ask_user.csx` and `ask_user\main.csx` hash differently, and only the flat form matches the bundled hash.

```
ask_user.csx  ->  f8a8110df3bbc9dd4535cee47d75c70a1899815e64ee1b87334a84505efdeb6b
main.csx      ->  ae8ca1439ad533e48824bfd9fd1762dce96283184c51fae6fb108654e2695362
```

## `tasks.csx`

| item | value |
|---|---|
| store | a folder holding `tasks\` and `arc.js`, found by walking up from the folder gatto opened |
| no store above | both tools return one sentence; both stay registered |
| seat | `gatto.json`: `{ "extensions": { "tasks": { "owner": "<seat>" } } }`; default `gatto`; must be declared in the store's `seats.md` |
| gatto 0.5.2 and earlier | refuse the `extensions` key; leave it out; seat is `gatto` |
| `task_write` add | runs one `git log` in the store's `tasks\` folder, so a deleted id is never reused |
| read class | requested in the script; inert until the file is vetted |

## `index.json`

The catalogue file. Consumers read this, not the repository layout.

```json
{
  "schema": 1,
  "extensions": [
    {
      "name": "ask_user",
      "file": "ask_user.csx",
      "version": 2,
      "description": "one line, for a person",
      "tools": ["ask_user"],
      "sha256": "d84cb1e4...",
      "bytes": 6513
    }
  ]
}
```

| field | meaning |
|---|---|
| `schema` | catalogue format version. Bumped only on a breaking change to these fields. |
| `name` | extension name. Equals `file` without the extension. |
| `file` | the script, at the repository root. |
| `version` | integer. Bumped whenever `file` changes. |
| `description` | one line for a person. The model-facing description is in the script's `Gatto.Register` call. |
| `tools` | every tool name the script registers. |
| `vetted` | optional. Absent means `true`. `false`: the file does not ship inside gatto, loads unvetted, and its tools ask for permission. A bundled file is never `false`. |
| `sha256` | SHA-256 of the file's bytes. Not gatto's vetting hash. |
| `bytes` | file size. |

Check a download:

```powershell
Get-FileHash ask_user.csx -Algorithm SHA256
```

## Byte identity

| fact | consequence |
|---|---|
| gatto grants the read class only to a vetted file | `ask_user` needs the grant |
| vetting is a content-hash match against the bundled copy | one byte of difference loads the file unvetted, with no error |
| the hash folds CRLF and lone CR to LF | a Windows checkout stays vetted |
| `.gitattributes` pins `* -text` | the bytes in the checkout are the bytes in the repository |

Rules:

1. Bundled files are not edited here. gatto produces them; they are copied out.
2. A change goes into gatto first and arrives here as its output.
3. `version`, `sha256` and `bytes` change in the same commit as the file.

## Build your own

Running your own extension needs no one's permission.
