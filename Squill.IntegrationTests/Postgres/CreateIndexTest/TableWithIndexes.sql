CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);

CREATE UNIQUE INDEX idx_film_title_unique ON film USING btree (title DESC NULLS LAST);
