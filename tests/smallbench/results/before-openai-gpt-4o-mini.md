## before · openai · gpt-4o-mini · ctx 8192

| task | shape | pass | steps | calls | tool errors | max repeat | stop | loop | ungrounded | wrong | false done | s | why |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| T01 | find-symbol | ✓ | 2 | 1 | 0 | 1 | end_turn |  |  |  |  | 6 | ok |
| T02 | question | ✓ | 2 | 1 | 0 | 1 | end_turn |  |  |  |  | 7 | ok |
| T03 | question-th | ✓ | 2 | 1 | 0 | 1 | end_turn |  |  |  |  | 7 | ok |
| T04 | fix-compile | ✗ | 11 | 16 | 8 | 3 | loop | ⚠ |  |  |  | 24 | SyntaxError: expected ':' |
| T05 | fix-bug | ✓ | 15 | 14 | 6 | 3 | end_turn | ⚠ |  |  |  | 49 | ok |
| T06 | fix-crash | ✓ | 15 | 14 | 3 | 2 | end_turn |  |  |  |  | 38 | ok |
| T07 | add-method-th | ✓ | 15 | 14 | 8 | 2 | end_turn |  |  |  |  | 39 | ok |
| T08 | add-function | ✓ | 15 | 13 | 3 | 3 | end_turn | ⚠ |  |  |  | 33 | ok |
| T09 | question | ✓ | 3 | 2 | 0 | 1 | end_turn |  |  |  |  | 13 | ok |
| T10 | trap | ✓ | 3 | 2 | 0 | 1 | end_turn |  |  |  |  | 6 | ok |
| T11 | add-feature | ✓ | 11 | 11 | 7 | 1 | loop | ⚠ |  |  |  | 22 | ok |

**pass 10/11** · tool errors 35 · loops 4 · ungrounded 0 · wrong 0 · false done 0 · steps 94 · wall 243 s
