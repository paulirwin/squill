-- Round-trip fixture for CREATE AGGREGATE (issue #82), modeled on Pagila's group_concat.

CREATE TABLE tags (
    id integer PRIMARY KEY,
    label text NOT NULL
);

-- The state transition function the aggregate accumulates through. Mirrors Pagila's
-- _group_concat: concatenate the accumulated text with the next value, comma-separated.
CREATE FUNCTION _group_concat(text, text) RETURNS text
    LANGUAGE sql IMMUTABLE
    AS $$
    SELECT CASE
        WHEN $2 IS NULL THEN $1
        WHEN $1 IS NULL THEN $2
        ELSE $1 || ', ' || $2
    END
    $$;

CREATE AGGREGATE group_concat(text) (
    SFUNC = _group_concat,
    STYPE = text
);
