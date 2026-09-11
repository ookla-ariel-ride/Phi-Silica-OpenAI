# Phi-3.5-mini tokenizer model

`tokenizer.model` is the SentencePiece model of
[microsoft/Phi-3.5-mini-instruct](https://huggingface.co/microsoft/Phi-3.5-mini-instruct)
(`tokenizer.model` at the repository root, 499,723 bytes, 32,000 pieces), downloaded on 2026-09-11.
It is embedded in `NpuBridge.Core` so builds and tests stay offline, and loaded by
`Phi3TokenCounter` through `Microsoft.ML.Tokenizers`'s `LlamaTokenizer`.

Why this file: Microsoft describes Phi Silica as based on a derivative of Phi-3.5-mini, and the
2026-09-11 measurement (`docs/DECISIONS.md` D80) found the runtime's prompt-length preflight lands on
3581 tokens of this tokenizer at every ASCII boundary tested, so its vocabulary is the runtime's.

License: MIT, Microsoft Corporation; the model's `LICENSE` file is beside this one.
