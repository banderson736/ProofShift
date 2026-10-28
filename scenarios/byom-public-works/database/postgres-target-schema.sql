CREATE TABLE IF NOT EXISTS public.assets
(
    asset_id varchar(8) PRIMARY KEY,
    asset_name varchar(80) NOT NULL,
    asset_type varchar(20) NOT NULL,
    active boolean NOT NULL
);

CREATE TABLE IF NOT EXISTS public.work_orders
(
    work_order_id varchar(8) PRIMARY KEY,
    asset_id varchar(8) NOT NULL,
    status varchar(16) NOT NULL,
    created_at date NOT NULL,
    description varchar(120) NOT NULL
);

CREATE TABLE IF NOT EXISTS public.work_order_history
(
    history_id varchar(8) PRIMARY KEY,
    work_order_id varchar(8) NOT NULL,
    status varchar(16) NOT NULL,
    changed_at date NOT NULL,
    note varchar(120) NOT NULL
);

CREATE TABLE IF NOT EXISTS public.asset_budgets
(
    budget_id varchar(8) PRIMARY KEY,
    budget_amount numeric(12,2) NOT NULL
);

CREATE ROLE proofshift_observer LOGIN PASSWORD '__BYOM_OBSERVER_PASSWORD__';
GRANT CONNECT ON DATABASE byom TO proofshift_observer;
GRANT USAGE ON SCHEMA public TO proofshift_observer;
GRANT SELECT ON public.assets, public.work_orders, public.work_order_history TO proofshift_observer;
