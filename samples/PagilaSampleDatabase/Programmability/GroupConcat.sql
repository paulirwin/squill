-- The _group_concat state-transition function and the group_concat user-defined AGGREGATE
-- built on top of it. Together they reproduce MySQL's GROUP_CONCAT in PostgreSQL: given two
-- text values, _group_concat concatenates them with a ", " separator (skipping NULLs), and
-- the aggregate folds that over a group. Several of the Sakila views (film_list, actor_info,
-- nicer_but_slower_film_list) depend on this aggregate, so it is deployed before them.
CREATE FUNCTION _group_concat(text, text)
RETURNS text
LANGUAGE sql
IMMUTABLE
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
