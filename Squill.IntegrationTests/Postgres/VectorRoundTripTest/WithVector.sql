CREATE EXTENSION vector;

CREATE TABLE items
(
    id        integer PRIMARY KEY,
    embedding vector(3)
);

CREATE INDEX items_embedding_idx ON items USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
