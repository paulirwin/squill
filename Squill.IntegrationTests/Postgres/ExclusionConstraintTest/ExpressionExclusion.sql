-- An expression key, whose derived constraint name is taken from the function name, and a
-- DESC ordering on a btree key.
CREATE TABLE account
(
    account_id integer PRIMARY KEY,
    email      text NOT NULL,
    priority   integer NOT NULL,
    EXCLUDE (lower(email) WITH =, priority DESC WITH =)
);
