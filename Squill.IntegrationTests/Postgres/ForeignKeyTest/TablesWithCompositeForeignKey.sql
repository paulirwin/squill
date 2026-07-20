CREATE TABLE orders
(
    id      integer NOT NULL,
    line_no integer NOT NULL,
    PRIMARY KEY (id, line_no)
);

CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    detail   varchar(200),
    CONSTRAINT fk_order_lines_orders FOREIGN KEY (order_id, line_no)
        REFERENCES orders (id, line_no) ON DELETE CASCADE ON UPDATE RESTRICT
);
