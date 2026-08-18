import type { Role } from "../tenants.js";
import type { ListParams } from "../api/inventory.js";

interface Props {
    params: ListParams;
    onParamsChange: (next: ListParams) => void;
    role: Role;
    onCreateNew?: () => void;
}

// Three variants keyed off `hasActiveFilters` + `role`:
//
//   1. Filters applied → "no matches, clear them" (nothing to onboard).
//   2. No filters + writer → a getting-started card that points at + New,
//      the bulk-upload panel above, and the CSV template link.
//   3. No filters + reader → explain the read-only nature and point at
//      the identity console so they can issue themselves a stronger key.
//
// The empty state renders in the same slot the old one-liner used, so no
// grid layout math changes. Compact enough to sit inside the virtualizer
// container without needing its own scroll region.
export function EmptyState({ params, onParamsChange, role, onCreateNew }: Props) {
    const hasFilters = hasActiveFilters(params);

    if (hasFilters) {
        return (
            <div className="flex flex-col items-start gap-2 px-4 py-6 text-sm text-slate-500">
                <span>No inventory matches these filters.</span>
                <button
                    type="button"
                    onClick={() => clearFilters(params, onParamsChange)}
                    className="rounded-md px-2 py-1 text-xs text-slate-700 ring-1 ring-inset ring-slate-300 hover:bg-slate-100"
                >
                    Clear filters
                </button>
            </div>
        );
    }

    if (role === "reader") {
        return (
            <div className="mx-auto my-8 max-w-lg rounded-lg border border-slate-200 bg-white p-6 shadow-sm">
                <h3 className="text-base font-semibold text-slate-900">Nothing here yet</h3>
                <p className="mt-2 text-sm text-slate-600">
                    Your key has <span className="font-medium text-slate-800">reader</span>{" "}
                    access, so you can view inventory but not create it. This tenant hasn&rsquo;t
                    had any rows added yet.
                </p>
                <ul className="mt-3 list-disc space-y-1 pl-5 text-sm text-slate-600">
                    <li>Ask an admin or worker on your tenant to add rows.</li>
                    <li>
                        Or issue yourself a key with more permissions from the identity console:{" "}
                        <a
                            href="https://auth.mettel.exercise.dany.codes/"
                            target="_blank"
                            rel="noopener noreferrer"
                            className="font-medium text-indigo-600 hover:text-indigo-500 hover:underline"
                        >
                            Manage account keys ↗
                        </a>
                    </li>
                </ul>
            </div>
        );
    }

    // Writer: admin or worker with a fresh, empty tenant.
    const isAdmin = role === "admin";
    return (
        <div className="mx-auto my-8 max-w-lg rounded-lg border border-slate-200 bg-white p-6 shadow-sm">
            <h3 className="text-base font-semibold text-slate-900">
                Welcome — this tenant is empty
            </h3>
            <p className="mt-2 text-sm text-slate-600">
                You&rsquo;re signed in as <span className="font-medium text-slate-800">{role}</span>.
                Add your first inventory row to get going.
            </p>
            <div className="mt-4 flex flex-wrap items-center gap-2">
                {onCreateNew && (
                    <button
                        type="button"
                        onClick={onCreateNew}
                        data-testid="empty-create-first"
                        className="rounded-md border border-gray-900 bg-gray-900 px-3 py-1.5 text-sm font-medium text-white hover:bg-slate-800"
                    >
                        + Create your first row
                    </button>
                )}
                <span className="text-xs text-slate-500">
                    or drag a CSV into the <span className="font-medium text-slate-700">Bulk upload</span> panel above.
                </span>
            </div>
            <p className="mt-4 text-xs text-slate-500">
                Need a starting file? Grab a{" "}
                <a
                    href="/api/v1/bulk-jobs/csv-template"
                    className="text-indigo-600 hover:text-indigo-500 hover:underline"
                >
                    CSV template
                </a>{" "}
                or the{" "}
                <a
                    href="/api/v1/bulk-jobs/sample-500k"
                    className="text-indigo-600 hover:text-indigo-500 hover:underline"
                >
                    500k row sample
                </a>
                .
            </p>
            {isAdmin && (
                <p className="mt-4 border-t border-slate-100 pt-3 text-xs text-slate-500">
                    Admins can also invite teammates from the{" "}
                    <a
                        href="https://auth.mettel.exercise.dany.codes/"
                        target="_blank"
                        rel="noopener noreferrer"
                        className="font-medium text-indigo-600 hover:text-indigo-500 hover:underline"
                    >
                        identity console ↗
                    </a>
                    .
                </p>
            )}
        </div>
    );
}

// Matches Filters.tsx's own hasActiveFilters check. Kept inline so the
// grid's empty state doesn't have to import the filter component just
// to answer the question.
export function hasActiveFilters(params: ListParams): boolean {
    return (params.q?.trim() ?? "") !== ""
        || (params.status?.length ?? 0) > 0
        || (params.productCategory?.length ?? 0) > 0
        || (params.state?.length ?? 0) > 0;
}

function clearFilters(params: ListParams, onChange: (next: ListParams) => void): void {
    onChange({
        ...params,
        q: undefined,
        fields: undefined,
        status: undefined,
        productCategory: undefined,
        state: undefined,
    });
}
