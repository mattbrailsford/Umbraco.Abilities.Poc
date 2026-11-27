import { css, html, nothing, repeat } from '@umbraco-cms/backoffice/external/lit';
import { UmbModalBaseElement } from '@umbraco-cms/backoffice/modal';
import { UmbTextStyles } from '@umbraco-cms/backoffice/style';

/**
 * The entity picker's dialog: Umbraco's item picker, with the items under a heading per group, such as a discount's
 * store, so entities with the same name in different groups can be told apart. Items with no group are listed first,
 * with no heading, so an entity type that doesn't group looks like Umbraco's own picker.
 *
 * Data: headline, and items ({ label, value, description?, icon?, group? }). Value: the picked item. Opened with
 * UMB_ABILITIES_ENTITY_PICKER_MODAL (entity-picker-modal.token.js).
 */

export default class UmbAbilitiesEntityPickerModalElement extends UmbModalBaseElement {
  static properties = {
    _query: { state: true },
  };

  constructor() {
    super();
    this._query = '';
  }

  // The items that match the filter, by group, in the order groups first appear. No group comes first.
  get #groups() {
    const query = this._query.toLowerCase();
    const groups = new Map([['', []]]);
    for (const item of this.data?.items ?? []) {
      if (query && !item.label.toLowerCase().includes(query) && !(item.group ?? '').toLowerCase().includes(query)) {
        continue;
      }

      const group = item.group ?? '';
      groups.set(group, [...(groups.get(group) ?? []), item]);
    }

    return [...groups].filter(([, items]) => items.length > 0);
  }

  #submit(item) {
    this.updateValue(item);
    this._submitModal();
  }

  render() {
    const groups = this.#groups;
    return html`
      <umb-body-layout headline=${this.data?.headline ?? 'Choose'}>
        <div id="main">
          <uui-input type="search" label="Filter" placeholder="Type to filter…" @input=${(e) => (this._query = e.target.value)}>
            <div slot="prepend"><uui-icon name="search"></uui-icon></div>
          </uui-input>
          ${groups.length
            ? repeat(
                groups,
                ([group]) => group,
                ([group, items]) => html`
                  <uui-box headline=${group || nothing}>
                    <uui-ref-list>
                      ${repeat(
                        items,
                        (item) => item.value,
                        (item) => html`
                          <umb-ref-item
                            name=${item.label}
                            detail=${item.description ?? nothing}
                            icon=${item.icon ?? nothing}
                            @open=${() => this.#submit(item)}></umb-ref-item>
                        `,
                      )}
                    </uui-ref-list>
                  </uui-box>
                `,
              )
            : html`<p>There are no items to select.</p>`}
        </div>
        <div slot="actions">
          <uui-button label="Close" @click=${this._rejectModal}></uui-button>
        </div>
      </umb-body-layout>
    `;
  }

  static styles = [
    UmbTextStyles,
    css`
      #main {
        display: flex;
        flex-direction: column;
        gap: var(--uui-size-space-5);
      }

      p {
        margin: 0;
      }
    `,
  ];
}

customElements.define('umb-abilities-entity-picker-modal', UmbAbilitiesEntityPickerModalElement);
