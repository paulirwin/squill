-- Parameterized operator classes on an index key (PostgreSQL 13+, issue #211).
--
-- gist/tsvector_ops takes a siglen parameter. Measured: the opclass here is the type's DEFAULT
-- opclass, yet PostgreSQL still requires it to be named when parameters are given, so this
-- fixture is what proves the extractor cannot suppress the name on opcdefault.
--
-- The indexes are declared in alphabetical order because the model hash is order-sensitive and
-- the extractor returns indexes sorted by name, while this source is read in declaration order.
CREATE TABLE docs
(
    id   integer PRIMARY KEY,
    body text,
    tsv  tsvector
);

-- A non-default opclass with no parameters: the pre-existing path, kept here so this fixture
-- would catch a regression in it.
CREATE INDEX idx_docs_body_pattern ON docs (body text_pattern_ops);

-- The same access method with no parameters, so the two forms are distinguished rather than
-- both being read as "has an opclass".
CREATE INDEX idx_docs_tsv_plain ON docs USING gist (tsv);

CREATE INDEX idx_docs_tsv_siglen ON docs USING gist (tsv tsvector_ops(siglen=256));
