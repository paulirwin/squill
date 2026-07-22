-- Junction table resolving the many-to-many relationship between films and actors. The
-- primary key is the composite (actor_id, film_id), declared as a table-level PRIMARY KEY
-- since it spans more than one column. An extra index on film_id backs the reverse lookup.
CREATE TABLE film_actor
(
    actor_id    integer NOT NULL REFERENCES actor (actor_id),
    film_id     integer NOT NULL REFERENCES film (film_id),
    last_update timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (actor_id, film_id)
);

CREATE INDEX idx_fk_film_id ON film_actor (film_id);
