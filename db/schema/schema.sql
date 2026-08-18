-- PosEdge multicaja schema (v1)
-- PostgreSQL 15+ recommended

-- Extensions: prefer idempotent DO blocks for broad compatibility
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgcrypto') THEN
    CREATE EXTENSION pgcrypto;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citext') THEN
    CREATE EXTENSION citext;
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS tenants (
  tenant_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz NULL
);

CREATE TABLE IF NOT EXISTS branches (
  branch_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  name text NOT NULL,
  timezone text NOT NULL DEFAULT 'America/Mexico_City',
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz NULL,
  UNIQUE (tenant_id, name)
);
CREATE INDEX IF NOT EXISTS ix_branches_tenant ON branches(tenant_id) WHERE deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS counters (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  key text NOT NULL,
  value bigint NOT NULL DEFAULT 0,
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, branch_id, key)
);

CREATE TABLE IF NOT EXISTS users (
  user_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  username citext NOT NULL,
  password_hash text NOT NULL,
  display_name text NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz NULL,
  UNIQUE (tenant_id, username)
);
CREATE INDEX IF NOT EXISTS ix_users_tenant ON users(tenant_id) WHERE deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS roles (
  role_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  name text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, name)
);

CREATE TABLE IF NOT EXISTS user_roles (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  user_id uuid NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
  role_id uuid NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
  PRIMARY KEY (tenant_id, user_id, role_id)
);

CREATE TABLE IF NOT EXISTS terminals (
  terminal_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  name text NOT NULL,
  machine_name text NOT NULL,
  status text NOT NULL DEFAULT 'active',
  cert_thumbprint text NULL,
  last_seen_at timestamptz NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz NULL,
  UNIQUE (tenant_id, branch_id, name)
);
CREATE INDEX IF NOT EXISTS ix_terminals_branch ON terminals(tenant_id, branch_id) WHERE deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS terminal_credentials (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id) ON DELETE CASCADE,
  issued_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NULL,
  cert_thumbprint text NOT NULL,
  revoked_at timestamptz NULL,
  PRIMARY KEY (tenant_id, terminal_id, cert_thumbprint)
);

-- ===== Terminal runtime status (server-side derived, mutable) =====
CREATE TABLE IF NOT EXISTS terminal_status (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id) ON DELETE CASCADE,
  updated_at timestamptz NOT NULL DEFAULT now(),
  last_seen_at timestamptz NULL,
  health_state text NULL,                   -- online|offline|replaying|degraded|divergent|recovering|waiting_replay|snapshot_backoff|revoked|recovering_degraded
  last_applied_seq bigint NULL,
  last_server_seq bigint NULL,
  replay_backlog integer NULL,
  dead_letter_count integer NULL,
  active_lease_id uuid NULL,
  divergence_state text NULL,
  snapshot_state text NULL,
  last_error text NULL,
  meta jsonb NULL,
  PRIMARY KEY (tenant_id, branch_id, terminal_id)
);
CREATE INDEX IF NOT EXISTS ix_terminal_status_state ON terminal_status(tenant_id, branch_id, health_state, updated_at DESC);

-- ===== Ops terminal commands (append-only) =====
-- NOTE: forbid_mutation() must be defined before creating triggers that reference it.
-- Some environments apply schema incrementally; keep a lightweight early definition here.
CREATE OR REPLACE FUNCTION forbid_mutation() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION 'append-only table: %', TG_TABLE_NAME;
END;
$$ LANGUAGE plpgsql;

CREATE TABLE IF NOT EXISTS ops_terminal_commands (
  command_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  request_id text NOT NULL,
  at timestamptz NOT NULL DEFAULT now(),
  command text NOT NULL,                    -- force_resync|force_snapshot|clear_divergence|revoke
  actor_id uuid NULL,
  reason text NULL,
  meta jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_ops_terminal_commands_time ON ops_terminal_commands(tenant_id, branch_id, at DESC);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_ops_terminal_commands') THEN
    CREATE TRIGGER tr_no_update_ops_terminal_commands
      BEFORE UPDATE OR DELETE ON ops_terminal_commands
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
END $$;

-- Append-only: record terminal command acknowledgements (instead of mutating ops_terminal_commands)
CREATE TABLE IF NOT EXISTS ops_terminal_command_acks (
  ack_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  command_id uuid NOT NULL REFERENCES ops_terminal_commands(command_id),
  request_id text NOT NULL,
  at timestamptz NOT NULL DEFAULT now(),
  meta jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (tenant_id, branch_id, command_id)
);
CREATE INDEX IF NOT EXISTS ix_ops_terminal_command_acks_time ON ops_terminal_command_acks(tenant_id, branch_id, at DESC);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_ops_terminal_command_acks') THEN
    CREATE TRIGGER tr_no_update_ops_terminal_command_acks
      BEFORE UPDATE OR DELETE ON ops_terminal_command_acks
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
END $$;

-- ===== Terminal replay mirror (server-side) =====
-- Terminals push a minimal mirror of their local queue so ops can inspect and act without touching the SQLite files.
CREATE TABLE IF NOT EXISTS terminal_replay_items (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  request_id text NOT NULL,
  type text NOT NULL,                       -- Sale.Commit, etc.
  state text NOT NULL,                      -- pending|inflight|retry_wait|dead_letter|purged|committed
  created_at_ms bigint NOT NULL,
  inflight_at_ms bigint NULL,
  attempt_count integer NOT NULL DEFAULT 0,
  last_error text NULL,
  next_retry_at_ms bigint NULL,
  offline_mode text NULL,
  lease_id uuid NULL,
  last_updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, branch_id, terminal_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_terminal_replay_state ON terminal_replay_items(tenant_id, branch_id, state, last_updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_terminal_replay_terminal ON terminal_replay_items(tenant_id, branch_id, terminal_id, state);
CREATE INDEX IF NOT EXISTS ix_terminal_replay_age ON terminal_replay_items(tenant_id, branch_id, created_at_ms);

CREATE TABLE IF NOT EXISTS terminal_dead_letters (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  request_id text NOT NULL,
  type text NOT NULL,
  dead_reason text NOT NULL,
  classification text NOT NULL,
  failed_at_ms bigint NOT NULL,
  retry_count integer NOT NULL DEFAULT 0,
  last_server_code text NULL,
  last_error text NULL,
  payload_snapshot_json text NOT NULL,
  PRIMARY KEY (tenant_id, branch_id, terminal_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_terminal_dead_letters_time ON terminal_dead_letters(tenant_id, branch_id, failed_at_ms DESC);

-- Append-only terminal replay log mirror for ops timeline/audit
CREATE TABLE IF NOT EXISTS terminal_replay_log (
  id bigserial PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  at_ms bigint NOT NULL,
  kind text NOT NULL,
  request_id text NULL,
  message text NOT NULL,
  meta_json text NULL
);
CREATE INDEX IF NOT EXISTS ix_terminal_replay_log_req ON terminal_replay_log(tenant_id, branch_id, request_id, at_ms);
CREATE INDEX IF NOT EXISTS ix_terminal_replay_log_time ON terminal_replay_log(tenant_id, branch_id, at_ms DESC);

-- Terminal ops hardening: allow server-side "stale/offline" classification and control-plane state.
ALTER TABLE terminal_status ADD COLUMN IF NOT EXISTS paused boolean NULL;
ALTER TABLE terminal_status ADD COLUMN IF NOT EXISTS paused_at timestamptz NULL;
ALTER TABLE terminal_status ADD COLUMN IF NOT EXISTS paused_reason text NULL;

CREATE TABLE IF NOT EXISTS ops_replay_actions (
  action_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  request_id text NOT NULL,                 -- idempotency key for this ops action
  at timestamptz NOT NULL DEFAULT now(),
  action text NOT NULL,                     -- retry|requeue|purge
  target_request_id text NULL,              -- replay requestId
  actor_id uuid NULL,
  reason text NULL,
  meta jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_ops_replay_actions_time ON ops_replay_actions(tenant_id, branch_id, at DESC);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_ops_replay_actions') THEN
    CREATE TRIGGER tr_no_update_ops_replay_actions
      BEFORE UPDATE OR DELETE ON ops_replay_actions
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS products (
  product_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  sku text NOT NULL,
  barcode text NULL,
  name text NOT NULL,
  unit text NOT NULL DEFAULT 'ea',
  tax_rate numeric(6,4) NOT NULL DEFAULT 0,
  price numeric(14,4) NOT NULL,
  cost numeric(14,4) NOT NULL DEFAULT 0,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz NULL,
  UNIQUE (tenant_id, sku)
);
CREATE INDEX IF NOT EXISTS ix_products_tenant_active ON products(tenant_id, is_active) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_products_barcode ON products(tenant_id, barcode) WHERE barcode IS NOT NULL AND deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS inventory (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  product_id uuid NOT NULL REFERENCES products(product_id),
  on_hand numeric(18,4) NOT NULL DEFAULT 0,
  reserved numeric(18,4) NOT NULL DEFAULT 0,
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, branch_id, product_id),
  CONSTRAINT ck_inventory_nonneg CHECK (on_hand >= 0 AND reserved >= 0)
);

-- ===== Offline leases (LEASES_ONLY) =====
CREATE TABLE IF NOT EXISTS inventory_leases (
  lease_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  request_id text NOT NULL,                  -- idempotency key for acquisition/renewal
  status text NOT NULL DEFAULT 'active',     -- active|expired|revoked
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  renewed_at timestamptz NULL,
  revoked_at timestamptz NULL,
  note text NULL,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_inventory_leases_active ON inventory_leases(tenant_id, branch_id, status, expires_at);
CREATE INDEX IF NOT EXISTS ix_inventory_leases_terminal ON inventory_leases(tenant_id, branch_id, terminal_id, status);

CREATE TABLE IF NOT EXISTS inventory_lease_lines (
  lease_id uuid NOT NULL REFERENCES inventory_leases(lease_id) ON DELETE CASCADE,
  product_id uuid NOT NULL REFERENCES products(product_id),
  qty_allocated numeric(18,4) NOT NULL CHECK (qty_allocated >= 0),
  qty_used numeric(18,4) NOT NULL DEFAULT 0 CHECK (qty_used >= 0),
  PRIMARY KEY (lease_id, product_id),
  CONSTRAINT ck_lease_used_le_alloc CHECK (qty_used <= qty_allocated)
);
CREATE INDEX IF NOT EXISTS ix_inventory_lease_lines_product ON inventory_lease_lines(product_id);

CREATE TABLE IF NOT EXISTS inventory_movements (
  movement_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  product_id uuid NOT NULL REFERENCES products(product_id),
  happened_at timestamptz NOT NULL DEFAULT now(),
  kind text NOT NULL,
  ref_type text NOT NULL,
  ref_id uuid NULL,
  delta numeric(18,4) NOT NULL,
  note text NULL,
  created_by uuid NULL REFERENCES users(user_id)
);
CREATE INDEX IF NOT EXISTS ix_inv_mov_by_product_time ON inventory_movements(tenant_id, branch_id, product_id, happened_at DESC);
CREATE INDEX IF NOT EXISTS ix_inv_mov_ref ON inventory_movements(tenant_id, ref_type, ref_id);

CREATE TABLE IF NOT EXISTS cash_sessions (
  cash_session_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  opened_by uuid NOT NULL REFERENCES users(user_id),
  opened_at timestamptz NOT NULL DEFAULT now(),
  closed_by uuid NULL REFERENCES users(user_id),
  closed_at timestamptz NULL,
  opening_amount numeric(14,4) NOT NULL DEFAULT 0,
  status text NOT NULL DEFAULT 'open'
);
CREATE INDEX IF NOT EXISTS ix_cash_sessions_open ON cash_sessions(tenant_id, branch_id, status) WHERE status='open';

-- Cash session hardening (close/reconciliation fields)
ALTER TABLE cash_sessions ADD COLUMN IF NOT EXISTS open_request_id text NULL;
ALTER TABLE cash_sessions ADD COLUMN IF NOT EXISTS close_request_id text NULL;
ALTER TABLE cash_sessions ADD COLUMN IF NOT EXISTS expected_amount numeric(14,4) NULL;
ALTER TABLE cash_sessions ADD COLUMN IF NOT EXISTS counted_amount numeric(14,4) NULL;
ALTER TABLE cash_sessions ADD COLUMN IF NOT EXISTS discrepancy_amount numeric(14,4) NULL;
ALTER TABLE cash_sessions ADD COLUMN IF NOT EXISTS reconciliation_status text NULL; -- balanced|discrepancy_open|resolved
ALTER TABLE cash_sessions ADD COLUMN IF NOT EXISTS last_cash_count_id uuid NULL;
ALTER TABLE cash_sessions ADD COLUMN IF NOT EXISTS note text NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_cash_sessions_one_open_per_terminal
  ON cash_sessions(tenant_id, branch_id, terminal_id)
  WHERE status='open';
CREATE UNIQUE INDEX IF NOT EXISTS ux_cash_sessions_open_request
  ON cash_sessions(tenant_id, branch_id, open_request_id)
  WHERE open_request_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_cash_sessions_close_request
  ON cash_sessions(tenant_id, branch_id, close_request_id)
  WHERE close_request_id IS NOT NULL;

-- Cash counts (arqueo) - append-only
CREATE TABLE IF NOT EXISTS cash_counts (
  cash_count_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  cash_session_id uuid NOT NULL REFERENCES cash_sessions(cash_session_id),
  request_id text NOT NULL,
  recorded_at timestamptz NOT NULL DEFAULT now(),
  counted_by uuid NULL REFERENCES users(user_id),
  total_counted numeric(14,4) NOT NULL,
  currency text NOT NULL DEFAULT 'MXN',
  meta jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_cash_counts_session_time ON cash_counts(tenant_id, cash_session_id, recorded_at DESC);

CREATE TABLE IF NOT EXISTS cash_count_lines (
  cash_count_id uuid NOT NULL REFERENCES cash_counts(cash_count_id) ON DELETE CASCADE,
  line_no integer NOT NULL,
  denomination numeric(14,4) NOT NULL CHECK (denomination > 0),
  quantity integer NOT NULL CHECK (quantity >= 0),
  amount numeric(14,4) NOT NULL,
  PRIMARY KEY (cash_count_id, line_no)
);
CREATE INDEX IF NOT EXISTS ix_cash_count_lines_denom ON cash_count_lines(denomination);

-- Cash discrepancy detection (append-only + resolution as separate append-only table)
CREATE TABLE IF NOT EXISTS cash_discrepancies (
  discrepancy_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  cash_session_id uuid NOT NULL REFERENCES cash_sessions(cash_session_id),
  request_id text NOT NULL,
  detected_at timestamptz NOT NULL DEFAULT now(),
  expected_amount numeric(14,4) NOT NULL,
  counted_amount numeric(14,4) NOT NULL,
  discrepancy_amount numeric(14,4) NOT NULL,
  severity text NOT NULL DEFAULT 'low', -- low|medium|high
  note text NULL,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_cash_discrepancies_session ON cash_discrepancies(tenant_id, cash_session_id, detected_at DESC);

CREATE TABLE IF NOT EXISTS cash_discrepancy_resolutions (
  resolution_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  discrepancy_id uuid NOT NULL REFERENCES cash_discrepancies(discrepancy_id),
  request_id text NOT NULL,
  resolved_at timestamptz NOT NULL DEFAULT now(),
  resolved_by uuid NULL REFERENCES users(user_id),
  resolution text NOT NULL, -- accepted|adjusted|investigate
  note text NULL,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_cash_discrepancy_resolutions_disc ON cash_discrepancy_resolutions(discrepancy_id, resolved_at DESC);

-- ===== Reconciliation findings (append-only) =====
CREATE TABLE IF NOT EXISTS reconciliation_findings (
  finding_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  finding_type text NOT NULL,               -- cash_mismatch|journal_imbalance|orphan_sale|orphan_payment|orphan_refund|orphan_outbox|stock_drift|...
  severity text NOT NULL DEFAULT 'low',     -- low|medium|high
  detected_at timestamptz NOT NULL DEFAULT now(),
  status text NOT NULL DEFAULT 'open',      -- open|ack|resolved
  entity_type text NULL,
  entity_id uuid NULL,
  cash_session_id uuid NULL REFERENCES cash_sessions(cash_session_id),
  request_id text NULL,
  message text NOT NULL,
  meta jsonb NOT NULL DEFAULT '{}'::jsonb,
  fingerprint text NOT NULL                -- for de-duplication per scan window
);
CREATE INDEX IF NOT EXISTS ix_recon_findings_time ON reconciliation_findings(tenant_id, branch_id, detected_at DESC);
CREATE INDEX IF NOT EXISTS ix_recon_findings_type ON reconciliation_findings(tenant_id, branch_id, finding_type, status);
CREATE UNIQUE INDEX IF NOT EXISTS ux_recon_fingerprint ON reconciliation_findings(tenant_id, branch_id, fingerprint);

-- Reconciliation actions (append-only) for ack/resolve workflows
CREATE TABLE IF NOT EXISTS reconciliation_actions (
  action_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  finding_id uuid NOT NULL REFERENCES reconciliation_findings(finding_id),
  at timestamptz NOT NULL DEFAULT now(),
  action text NOT NULL,                      -- ack|resolve|note
  actor_type text NOT NULL,                  -- ops_user|system
  actor_id uuid NULL,
  request_id text NOT NULL,
  note text NULL,
  meta jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_recon_actions_finding_time ON reconciliation_actions(tenant_id, finding_id, at DESC);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_reconciliation_findings') THEN
    CREATE TRIGGER tr_no_update_reconciliation_findings
      BEFORE UPDATE OR DELETE ON reconciliation_findings
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_reconciliation_actions') THEN
    CREATE TRIGGER tr_no_update_reconciliation_actions
      BEFORE UPDATE OR DELETE ON reconciliation_actions
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_cash_counts') THEN
    CREATE TRIGGER tr_no_update_cash_counts
      BEFORE UPDATE OR DELETE ON cash_counts
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_cash_count_lines') THEN
    CREATE TRIGGER tr_no_update_cash_count_lines
      BEFORE UPDATE OR DELETE ON cash_count_lines
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_cash_discrepancies') THEN
    CREATE TRIGGER tr_no_update_cash_discrepancies
      BEFORE UPDATE OR DELETE ON cash_discrepancies
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_cash_discrepancy_resolutions') THEN
    CREATE TRIGGER tr_no_update_cash_discrepancy_resolutions
      BEFORE UPDATE OR DELETE ON cash_discrepancy_resolutions
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS cash_movements (
  cash_movement_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  cash_session_id uuid NOT NULL REFERENCES cash_sessions(cash_session_id),
  happened_at timestamptz NOT NULL DEFAULT now(),
  kind text NOT NULL,
  reason text NOT NULL,
  amount numeric(14,4) NOT NULL CHECK (amount > 0),
  note text NULL,
  created_by uuid NOT NULL REFERENCES users(user_id)
);
CREATE INDEX IF NOT EXISTS ix_cash_mov_session_time ON cash_movements(tenant_id, cash_session_id, happened_at DESC);

CREATE TABLE IF NOT EXISTS cash_ledger_entries (
  entry_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  cash_session_id uuid NOT NULL REFERENCES cash_sessions(cash_session_id),
  happened_at timestamptz NOT NULL DEFAULT now(),
  kind text NOT NULL,
  ref_type text NOT NULL,
  ref_id uuid NULL,
  amount numeric(14,4) NOT NULL,
  currency text NOT NULL DEFAULT 'MXN',
  created_by uuid NULL REFERENCES users(user_id),
  note text NULL
);
CREATE INDEX IF NOT EXISTS ix_cash_ledger_session_time ON cash_ledger_entries(tenant_id, cash_session_id, happened_at DESC);

-- Cash tables should be append-only (operational hardening)
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_cash_movements') THEN
    CREATE TRIGGER tr_no_update_cash_movements
      BEFORE UPDATE OR DELETE ON cash_movements
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_cash_ledger_entries') THEN
    CREATE TRIGGER tr_no_update_cash_ledger_entries
      BEFORE UPDATE OR DELETE ON cash_ledger_entries
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
END $$;

-- ===== Financial journal (append-only, double-entry capable) =====
CREATE TABLE IF NOT EXISTS financial_journal (
  journal_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  request_id text NOT NULL,                 -- idempotency/correlation
  at timestamptz NOT NULL DEFAULT now(),
  kind text NOT NULL,                       -- sale.commit | sale.void | refund | cash.open | cash.close | ...
  ref_type text NOT NULL,                   -- Sale | CashSession | ...
  ref_id uuid NULL,
  terminal_id uuid NULL REFERENCES terminals(terminal_id),
  cash_session_id uuid NULL REFERENCES cash_sessions(cash_session_id),
  currency text NOT NULL DEFAULT 'MXN',
  total_debit numeric(14,4) NOT NULL DEFAULT 0,
  total_credit numeric(14,4) NOT NULL DEFAULT 0,
  meta jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_fin_journal_time ON financial_journal(tenant_id, branch_id, at DESC);
CREATE INDEX IF NOT EXISTS ix_fin_journal_ref ON financial_journal(tenant_id, ref_type, ref_id);

CREATE TABLE IF NOT EXISTS financial_journal_lines (
  journal_id uuid NOT NULL REFERENCES financial_journal(journal_id) ON DELETE CASCADE,
  line_no integer NOT NULL,
  account text NOT NULL,                    -- e.g. cash, card, sales_revenue, tax_payable, refunds, over_short
  debit numeric(14,4) NOT NULL DEFAULT 0,
  credit numeric(14,4) NOT NULL DEFAULT 0,
  note text NULL,
  PRIMARY KEY (journal_id, line_no),
  CONSTRAINT ck_fin_line_nonneg CHECK (debit >= 0 AND credit >= 0),
  CONSTRAINT ck_fin_line_not_both CHECK (NOT (debit > 0 AND credit > 0))
);
CREATE INDEX IF NOT EXISTS ix_fin_journal_lines_account ON financial_journal_lines(account);

-- ===== Immutable audit log (append-only) =====
CREATE TABLE IF NOT EXISTS audit_log (
  audit_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  at timestamptz NOT NULL DEFAULT now(),
  actor_type text NOT NULL,                 -- terminal | user | system
  actor_id uuid NULL,
  tenant_id uuid NULL,
  branch_id uuid NULL,
  request_id text NULL,
  action text NOT NULL,                     -- sale.commit | sale.void | refund | admission.claim | lease.revoke | ...
  entity_type text NULL,
  entity_id uuid NULL,
  classification text NOT NULL DEFAULT 'ops', -- ops|finance|security|sync
  ip inet NULL,
  user_agent text NULL,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX IF NOT EXISTS ix_audit_time ON audit_log(at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_req ON audit_log(request_id) WHERE request_id IS NOT NULL;

-- Enforce append-only semantics
CREATE OR REPLACE FUNCTION forbid_mutation() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION 'append-only table: %', TG_TABLE_NAME;
END;
$$ LANGUAGE plpgsql;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_financial_journal') THEN
    CREATE TRIGGER tr_no_update_financial_journal
      BEFORE UPDATE OR DELETE ON financial_journal
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_financial_journal_lines') THEN
    CREATE TRIGGER tr_no_update_financial_journal_lines
      BEFORE UPDATE OR DELETE ON financial_journal_lines
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_audit_log') THEN
    CREATE TRIGGER tr_no_update_audit_log
      BEFORE UPDATE OR DELETE ON audit_log
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS sales (
  sale_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  cash_session_id uuid NOT NULL REFERENCES cash_sessions(cash_session_id),
  request_id text NOT NULL,
  server_ticket bigint NOT NULL,
  status text NOT NULL DEFAULT 'committed',
  currency text NOT NULL DEFAULT 'MXN',
  subtotal numeric(14,4) NOT NULL,
  tax numeric(14,4) NOT NULL,
  total numeric(14,4) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  voided_at timestamptz NULL,
  void_reason text NULL,
  UNIQUE (tenant_id, branch_id, request_id),
  UNIQUE (tenant_id, branch_id, server_ticket)
);
CREATE INDEX IF NOT EXISTS ix_sales_branch_time ON sales(tenant_id, branch_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_sales_session ON sales(tenant_id, cash_session_id, created_at DESC);

-- ===== Refunds & credit notes (append-only) =====
CREATE TABLE IF NOT EXISTS refunds (
  refund_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  sale_id uuid NOT NULL REFERENCES sales(sale_id),
  request_id text NOT NULL,
  terminal_id uuid NULL REFERENCES terminals(terminal_id),
  cash_session_id uuid NULL REFERENCES cash_sessions(cash_session_id),
  kind text NOT NULL DEFAULT 'refund',        -- refund|credit_note
  status text NOT NULL DEFAULT 'posted',      -- posted|reversed
  created_at timestamptz NOT NULL DEFAULT now(),
  currency text NOT NULL DEFAULT 'MXN',
  subtotal numeric(14,4) NOT NULL,
  tax numeric(14,4) NOT NULL,
  total numeric(14,4) NOT NULL,
  note text NULL,
  UNIQUE (tenant_id, branch_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_refunds_sale ON refunds(tenant_id, branch_id, sale_id, created_at DESC);

CREATE TABLE IF NOT EXISTS refund_lines (
  refund_id uuid NOT NULL REFERENCES refunds(refund_id) ON DELETE CASCADE,
  sale_line_id uuid NOT NULL REFERENCES sale_lines(sale_line_id),
  product_id uuid NOT NULL REFERENCES products(product_id),
  qty numeric(18,4) NOT NULL CHECK (qty > 0),
  unit_price numeric(14,4) NOT NULL CHECK (unit_price >= 0),
  tax_rate numeric(6,4) NOT NULL DEFAULT 0,
  line_subtotal numeric(14,4) NOT NULL,
  line_tax numeric(14,4) NOT NULL,
  line_total numeric(14,4) NOT NULL,
  PRIMARY KEY (refund_id, sale_line_id)
);
CREATE INDEX IF NOT EXISTS ix_refund_lines_product ON refund_lines(product_id);

CREATE TABLE IF NOT EXISTS refund_payments (
  refund_payment_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  refund_id uuid NOT NULL REFERENCES refunds(refund_id) ON DELETE CASCADE,
  method text NOT NULL,
  amount numeric(14,4) NOT NULL CHECK (amount >= 0),
  currency text NOT NULL DEFAULT 'MXN',
  meta jsonb NULL
);
CREATE INDEX IF NOT EXISTS ix_refund_payments_refund ON refund_payments(refund_id);

-- Prevent double-refund beyond sale lines (server-enforced aggregation)
-- CREATE VIEW IF NOT EXISTS is not supported on older PostgreSQL versions; use CREATE OR REPLACE for portability.
CREATE OR REPLACE VIEW v_refunded_qty_by_sale_line AS
SELECT r.tenant_id, r.branch_id, rl.sale_line_id, SUM(rl.qty) AS refunded_qty
  FROM refunds r
  JOIN refund_lines rl ON rl.refund_id = r.refund_id
 WHERE r.status = 'posted'
 GROUP BY r.tenant_id, r.branch_id, rl.sale_line_id;

-- Append-only enforcement for financial refund tables
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_refunds') THEN
    CREATE TRIGGER tr_no_update_refunds
      BEFORE UPDATE OR DELETE ON refunds
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_refund_lines') THEN
    CREATE TRIGGER tr_no_update_refund_lines
      BEFORE UPDATE OR DELETE ON refund_lines
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tr_no_update_refund_payments') THEN
    CREATE TRIGGER tr_no_update_refund_payments
      BEFORE UPDATE OR DELETE ON refund_payments
      FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS sale_lines (
  sale_line_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  sale_id uuid NOT NULL REFERENCES sales(sale_id) ON DELETE CASCADE,
  product_id uuid NOT NULL REFERENCES products(product_id),
  sku text NOT NULL,
  name text NOT NULL,
  qty numeric(18,4) NOT NULL CHECK (qty > 0),
  unit_price numeric(14,4) NOT NULL CHECK (unit_price >= 0),
  tax_rate numeric(6,4) NOT NULL DEFAULT 0,
  line_subtotal numeric(14,4) NOT NULL,
  line_tax numeric(14,4) NOT NULL,
  line_total numeric(14,4) NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_sale_lines_sale ON sale_lines(sale_id);
CREATE INDEX IF NOT EXISTS ix_sale_lines_product ON sale_lines(product_id);

CREATE TABLE IF NOT EXISTS payments (
  payment_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  sale_id uuid NOT NULL REFERENCES sales(sale_id) ON DELETE CASCADE,
  method text NOT NULL,
  amount numeric(14,4) NOT NULL CHECK (amount >= 0),
  currency text NOT NULL DEFAULT 'MXN',
  meta jsonb NULL
);
CREATE INDEX IF NOT EXISTS ix_payments_sale ON payments(sale_id);

CREATE TABLE IF NOT EXISTS idempotency_responses (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  request_id text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  status_code text NOT NULL,
  response jsonb NOT NULL,
  PRIMARY KEY (tenant_id, branch_id, request_id)
);

-- ===== Admission control (replay/snapshot throttling) =====
CREATE TABLE IF NOT EXISTS admission_slots (
  slot_kind text NOT NULL,                  -- 'replay' | 'snapshot'
  slot_id integer NOT NULL,
  claimed_by uuid NULL,
  claimed_token text NULL,
  claimed_at timestamptz NULL,
  claimed_until timestamptz NULL,
  PRIMARY KEY (slot_kind, slot_id)
);
CREATE INDEX IF NOT EXISTS ix_admission_slots_available ON admission_slots(slot_kind, claimed_until);
CREATE INDEX IF NOT EXISTS ix_admission_slots_token ON admission_slots(slot_kind, claimed_token) WHERE claimed_token IS NOT NULL;

DO $$
BEGIN
  -- Seed slots if none exist (safe to re-run).
  IF NOT EXISTS (SELECT 1 FROM admission_slots WHERE slot_kind='replay') THEN
    INSERT INTO admission_slots(slot_kind, slot_id)
    SELECT 'replay', s FROM generate_series(1, 10) s;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM admission_slots WHERE slot_kind='snapshot') THEN
    INSERT INTO admission_slots(slot_kind, slot_id)
    SELECT 'snapshot', s FROM generate_series(1, 2) s;
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS event_log (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  seq bigint GENERATED ALWAYS AS IDENTITY,
  at timestamptz NOT NULL DEFAULT now(),
  type text NOT NULL,
  entity_type text NOT NULL,
  entity_id uuid NULL,
  terminal_id uuid NULL REFERENCES terminals(terminal_id),
  request_id text NULL,
  data jsonb NOT NULL,
  PRIMARY KEY (tenant_id, branch_id, seq)
);
CREATE INDEX IF NOT EXISTS ix_event_log_at ON event_log(tenant_id, branch_id, at DESC);
CREATE INDEX IF NOT EXISTS ix_event_log_request ON event_log(tenant_id, branch_id, request_id) WHERE request_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS outbox (
  outbox_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  created_at timestamptz NOT NULL DEFAULT now(),
  topic text NOT NULL,
  payload jsonb NOT NULL,
  status text NOT NULL DEFAULT 'pending',
  attempts int NOT NULL DEFAULT 0,
  last_error text NULL,
  next_retry_at timestamptz NULL
);
CREATE INDEX IF NOT EXISTS ix_outbox_pending ON outbox(status, created_at) WHERE status='pending';

CREATE TABLE IF NOT EXISTS terminal_sync_state (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id),
  branch_id uuid NOT NULL REFERENCES branches(branch_id),
  terminal_id uuid NOT NULL REFERENCES terminals(terminal_id),
  last_applied_seq bigint NOT NULL DEFAULT 0,
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, branch_id, terminal_id)
);

CREATE OR REPLACE FUNCTION set_updated_at() RETURNS trigger AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='tr_tenants_updated') THEN
    CREATE TRIGGER tr_tenants_updated BEFORE UPDATE ON tenants FOR EACH ROW EXECUTE FUNCTION set_updated_at();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='tr_branches_updated') THEN
    CREATE TRIGGER tr_branches_updated BEFORE UPDATE ON branches FOR EACH ROW EXECUTE FUNCTION set_updated_at();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='tr_users_updated') THEN
    CREATE TRIGGER tr_users_updated BEFORE UPDATE ON users FOR EACH ROW EXECUTE FUNCTION set_updated_at();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='tr_terminals_updated') THEN
    CREATE TRIGGER tr_terminals_updated BEFORE UPDATE ON terminals FOR EACH ROW EXECUTE FUNCTION set_updated_at();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='tr_products_updated') THEN
    CREATE TRIGGER tr_products_updated BEFORE UPDATE ON products FOR EACH ROW EXECUTE FUNCTION set_updated_at();
  END IF;
END $$;

