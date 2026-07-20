CREATE TABLE book
(
    book_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    author_id integer NOT NULL REFERENCES author (author_id),
    title varchar(400) NOT NULL
);

CREATE INDEX ix_book_author_id ON book (author_id);
