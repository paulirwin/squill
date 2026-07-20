CREATE EXTENSION citext;

CREATE TABLE contacts
(
    id    integer PRIMARY KEY,
    email varchar(255) NOT NULL
);
