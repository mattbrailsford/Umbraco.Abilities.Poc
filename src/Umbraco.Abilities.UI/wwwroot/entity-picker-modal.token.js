import { UmbModalToken } from '@umbraco-cms/backoffice/modal';

/**
 * Opens the entity picker's dialog (entity-picker-modal.js, registered in umbraco-package.json). Kept apart from the
 * dialog, so opening it doesn't load the dialog's module a second time: the backoffice loads that itself.
 */
export const UMB_ABILITIES_ENTITY_PICKER_MODAL = new UmbModalToken('Umb.Abilities.Modal.EntityPicker', {
  modal: { type: 'sidebar', size: 'small' },
});
