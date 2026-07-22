-- Languages a film can be recorded in. Note name is a fixed-width char(20) (blank-padded),
-- matching the canonical Sakila schema rather than a varchar.
CREATE TABLE language
(
    language_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        char(20) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);
