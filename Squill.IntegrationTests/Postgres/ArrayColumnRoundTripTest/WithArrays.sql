CREATE TABLE films
(
    id               integer PRIMARY KEY,
    special_features text[],
    tags             varchar[],
    scores           integer[]
);
