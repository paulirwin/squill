CREATE TABLE account
(
    account_id integer PRIMARY KEY,
    email      varchar(255) NOT NULL
);

CREATE UNIQUE INDEX idx_account_email ON account USING btree (email DESC NULLS LAST);
