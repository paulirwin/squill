-- Physical inventory: each row is a copy of a film held at a particular store. The composite
-- index on (store_id, film_id) supports the common "what copies of this film does this store
-- have" lookup.
CREATE TABLE inventory
(
    inventory_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    film_id      integer NOT NULL REFERENCES film (film_id),
    store_id     integer NOT NULL REFERENCES store (store_id),
    last_update  timestamp NOT NULL DEFAULT now()
);

CREATE INDEX idx_store_id_film_id ON inventory (store_id, film_id);
