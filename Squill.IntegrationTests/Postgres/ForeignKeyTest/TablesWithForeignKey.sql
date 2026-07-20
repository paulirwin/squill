CREATE TABLE customers
(
    id   integer PRIMARY KEY,
    name varchar(100) NOT NULL
);

CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customers (id) ON DELETE CASCADE
);
