import { LitElement, html, css } from '@umbraco-cms/backoffice/external/lit';
import { UmbElementMixin } from '@umbraco-cms/backoffice/element-api';
import { UmbChangeEvent } from '@umbraco-cms/backoffice/event';
import { UMB_MODAL_MANAGER_CONTEXT } from '@umbraco-cms/backoffice/modal';
import { UMB_PROPERTY_DATASET_CONTEXT } from '@umbraco-cms/backoffice/property';
import { UMB_ABILITIES_ENTITY_PICKER_MODAL } from './entity-picker-modal.token.js';
import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';

/**
 * Picks one entity of any entity type that has an adapter, such as a Commerce discount: the generic picker for a key
 * marked x-references, where the product has no picker that works outside its own screens. It lists the entities by
 * calling entity.find through the Management API, as the form's consumer and the signed-in user, so it only offers
 * what that user may see. Entities with a group, such as a discount's store, are listed under it, and the group is shown
 * under the picked one. Its value is the picked entity's key.
 *
 * A picker within another field's entity, such as a discount within the chosen store, lists only what is within it
 * once that is chosen, and everything, by group, until then.
 *
 * Config, from the automate-editors filter: entityType, consumer, and withinField (the alias of the field whose entity
 * this one must be within). FUDGE (dependent config): the picker reads that field from its form, as a form host can't
 * yet give an editor settings that follow another field. With that, the host would pass the key as `within`.
 */
export default class UmbAbilitiesEntityPickerElement extends UmbElementMixin(LitElement) {
  static properties = {
    value: { type: String },
    config: { attribute: false },
    _entities: { state: true },
    _state: { state: true },
    _error: { state: true },
  };

  #entityType;
  #consumer = 'automate';
  #withinField;
  #within;
  #dataset;

  constructor() {
    super();
    this._entities = undefined;
    this.consumeContext(UMB_PROPERTY_DATASET_CONTEXT, (dataset) => {
      this.#dataset = dataset;
      this.#watch();
    });
  }

  set config(config) {
    const get = (alias) => (Array.isArray(config) ? config.find((c) => c.alias === alias)?.value : config?.getValueByAlias?.(alias));
    this.#entityType = get('entityType');
    this.#consumer = get('consumer') ?? this.#consumer;
    this.#withinField = get('withinField');
    this.#watch();
    this.#load();
  }

  // The field this picker is within: list again whenever it changes.
  async #watch() {
    if (!this.#dataset || !this.#withinField) {
      return;
    }

    const values = await this.#dataset.propertyValueByAlias(this.#withinField);
    this.observe(
      values,
      (value) => {
        const key = Array.isArray(value) ? value[0]?.unique ?? value[0] : value;
        if (key === this.#within) {
          return;
        }

        this.#within = key;
        this.#load();
      },
      'abilities-within',
    );
  }

  async #load() {
    if (!this.#entityType) {
      return;
    }

    this._state = 'waiting';
    const { data, error } = await umbHttpClient.post({
      url: '/umbraco/management/api/v1/abilities/entity.find/invoke',
      body: { entityType: this.#entityType, query: '', ...(this.#within ? { within: this.#within } : {}) },
      headers: { 'Content-Type': 'application/json', 'Umb-Consumer': this.#consumer },
      security: [{ scheme: 'bearer', type: 'http' }],
    });
    this._state = undefined;
    this._error = error || !data?.success ? 'The list could not be loaded.' : undefined;
    this._entities = data?.success ? data.result ?? [] : [];
  }

  get #picked() {
    return this.value ? this._entities?.find((e) => e.key === this.value) : undefined;
  }

  async #choose() {
    const manager = await this.getContext(UMB_MODAL_MANAGER_CONTEXT);
    const modal = manager.open(this, UMB_ABILITIES_ENTITY_PICKER_MODAL, {
      data: {
        headline: 'Choose',
        items: (this._entities ?? []).map((e) => ({ label: e.name, description: e.subtype ?? undefined, value: e.key, icon: 'icon-link', group: e.group?.name })),
      },
    });
    const picked = await modal.onSubmit().catch(() => undefined);
    if (picked?.value) {
      this.#set(picked.value);
    }
  }

  #set(value) {
    this.value = value;
    this.dispatchEvent(new UmbChangeEvent());
  }

  render() {
    if (this._error) {
      return html`<p class="error">${this._error}</p>`;
    }

    if (this.value) {
      const picked = this.#picked;
      return html`
        <uui-ref-list>
          <uui-ref-node name=${picked?.name ?? (this._entities ? (this.#within ? 'Not in the chosen one' : 'Not found') : '…')} detail=${[picked?.group?.name, picked?.subtype].filter(Boolean).join(' · ')} standalone>
            <uui-icon slot="icon" name="icon-link"></uui-icon>
            <uui-action-bar slot="actions">
              <uui-button label="Remove" @click=${() => this.#set(undefined)}>Remove</uui-button>
            </uui-action-bar>
          </uui-ref-node>
        </uui-ref-list>
      `;
    }

    return html`
      <uui-button id="choose" look="placeholder" label="Choose" .state=${this._state} ?disabled=${!this._entities?.length} @click=${this.#choose}>
        ${this._entities && this._entities.length === 0 ? 'Nothing to choose' : 'Choose'}
      </uui-button>
    `;
  }

  static styles = css`
    :host {
      display: block;
      width: 100%;
    }

    #choose {
      width: 100%;
    }

    .error {
      color: var(--uui-color-danger);
      margin: 0;
    }
  `;
}

customElements.define('umb-abilities-entity-picker', UmbAbilitiesEntityPickerElement);
