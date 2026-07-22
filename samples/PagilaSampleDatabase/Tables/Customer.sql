-- Customers of a store. Note the historical quirk carried over from Sakila: activebool is the
-- real boolean active flag, while the separate integer "active" column is a legacy marker.
-- last_update here is nullable (unlike most tables), matching the canonical schema.
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    store_id    integer NOT NULL REFERENCES store (store_id),
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    email       varchar(50),
    address_id  integer NOT NULL REFERENCES address (address_id),
    activebool  boolean NOT NULL DEFAULT true,
    create_date date NOT NULL DEFAULT now(),
    last_update timestamp DEFAULT now(),
    active      integer
);

CREATE INDEX idx_fk_address_id ON customer (address_id);
CREATE INDEX idx_fk_store_id ON customer (store_id);
CREATE INDEX idx_last_name ON customer (last_name);
