# Evaluation protocols (M5)

Three independent lanes assess a **completed** research record. Evaluation is separate from
execution completion and from publication authorization: a job completing technically does not make
its research accurate, and an evaluation judgment never rewrites execution history.

- **Lane 1 — Technical integrity** (`TechnicalIntegrityEvaluator`): deterministic checks — reference
  integrity (every committed finding cites captured evidence), report schema (cites only committed
  findings), budget enforcement, disposition present.
- **Lane 2 — Semantic evidence** (`SemanticEvaluator`): local-model-assisted assessment of each claim
  against its own evidence (supported / contradicted / insufficient / conflicting). A model's
  assessment is **not** a certificate of truth — anything unparseable or out-of-vocabulary, or any
  transport/timeout failure, becomes `Unknown`. Uncertainty is preserved.
- **Lane 3 — Evaluation laboratory** (`LaboratoryEvaluator`): frozen cases with reference evidence and
  expected outcomes (`evaluation/fixtures/`). Fixtures are **synthetic** and stay `provisional: true`
  until independently reviewed; they are never presented as validated research truth.

Record for each run: model digest, prompt version, evidence package, protocol version, assessment
output, failure categories, runtime, usage (spec §26). Comparing approved models must not silently
alter the benchmark dataset.
